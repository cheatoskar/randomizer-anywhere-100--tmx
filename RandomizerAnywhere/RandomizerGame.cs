using ManiaAPI.XmlRpc;
using RandomizerAnywhere.Config;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using TmEssentials;

namespace RandomizerAnywhere;

internal sealed partial class RandomizerGame
{
    // give players time to manually save their replay (in-game "save replay" key) before
    // the map gets forcefully switched away from under them
    private const int ReplaySaveGraceMs = 8000;

    private const int PresetVoteWindowMs = 30000;

    // AGPLv3 §13: this is a modified version, so users interacting with it over the network
    private const string SourceRepoUrl = "http://github.com/cheatoskar/randomizer-anywhere-100--tmx";

    private readonly RemoteClient client;
    private readonly TmxRules tmxRules;
    private readonly AppConfig config;
    private readonly DiscordNotifier discordNotifier;
    private readonly Leaderboard leaderboard;

    private readonly Dictionary<string, Func<int, string, string[], CancellationToken, Task>> commandHandlers;
    private readonly Dictionary<string, string> nicknameCache = [];

    private Stopwatch? sessionStopwatch;
    private int sessionStopwatchMillisecondOffset;
    private MapInfo? currentMap;
    private int? currentMapTrackId;
    private int? pendingMapTrackId;
    private string? randomEnqueuedMapFileName;

    private string? votePresetName;
    private HashSet<string>? voteYesLogins;

    // guards against concurrent map advances - with multiple players and AutoSkipMode=Finished,
    // every player finishing the same map fires its own PlayerFinish -> NextRandomMapAsync call;
    // without this, a second call arriving while the first is still in flight would skip the
    // "fetch a new map" step (one is already queued) but still send a second NextChallenge,
    // advancing the dedicated server one extra map past what pendingMapTrackId/currentMapTrackId
    // account for - which is exactly what made /map, /imp and /hard report the previous map
    private bool isAdvancingToNextMap;

    private int? currentMapCheckpointTotal;
    private readonly Dictionary<string, int> playerCheckpointProgress = [];
    // tie-breaks the live "who's leading" ranking on the status page: same checkpoint count ->
    // whoever reached it first is ahead
    private readonly Dictionary<string, DateTimeOffset> playerCheckpointTimestamp = [];

    private bool SessionActive => sessionStopwatch is not null;

    public RandomizerGame(RemoteClient client, TmxRules tmxRules, AppConfig config, DiscordNotifier discordNotifier, Leaderboard leaderboard)
    {
        this.client = client;
        this.tmxRules = tmxRules;
        this.config = config;
        this.discordNotifier = discordNotifier;
        this.leaderboard = leaderboard;

        commandHandlers = new()
        {
            ["start"] = StartAsync,
            ["stop"] = StopAsync,
            ["end"] = StopAsync,
            ["skip"] = SkipAsync,
            ["imp"] = ImpossibleAsync,
            ["hard"] = HardAsync,
            ["top"] = TopAsync,
            ["rank"] = RankAsync,
            ["map"] = MapAsync,
            ["rounds"] = RoundsAsync,
            ["info"] = InfoAsync,
            ["testhud"] = TestHudAsync,
            ["votepreset"] = VotePresetAsync,
            ["yes"] = YesAsync,
            ["no"] = NoAsync,
            ["commands"] = CommandsAsync,
            ["source"] = SourceAsync,
            ["timelimit"] = TimeLimitAsync,
            ["tl"] = TimeLimitAsync,
            ["preset"] = PresetAsync,
            ["presets"] = PresetsAsync
        };

        /*client.Callback += async (methodName, methodParams, cancellationToken) =>
        {
            Console.WriteLine($"{methodName} {string.Join(' ', methodParams.Select(x =>
            {
                return x is Dictionary<string, object> dict
                    ? $"{{{string.Join(", ", dict.Select(kv => $"{kv.Key}: {kv.Value}"))}}}"
                    : x?.ToString() ?? "null";
            }))}");
        };*/
    }

    private void RegisterCallbacks()
    {
        client.On("TrackMania.BeginRace", async (methodParams, cancellationToken) =>
        {
            try
            {
                var mapInfo = (Dictionary<string, object>)methodParams[0];

                currentMap = new MapInfo(
                    AuthorTime: (int)mapInfo["AuthorTime"],
                    GoldTime: (int)mapInfo["GoldTime"],
                    SilverTime: (int)mapInfo["SilverTime"],
                    BronzeTime: (int)mapInfo["BronzeTime"]
                );
                currentMapTrackId = pendingMapTrackId;

                playerCheckpointProgress.Clear();
                playerCheckpointTimestamp.Clear();
                currentMapCheckpointTotal = null;

                var info = await client.GetCurrentChallengeInfoAsync(cancellationToken);
                currentMapCheckpointTotal = info.NbCheckpoints;

                if (info.NbCheckpoints is { } total)
                {
                    await client.SendManialinkPageAsync(BuildCheckpointManialink(0, total), cancellationToken: cancellationToken);
                }

                if (info.LapRace)
                {
                    await client.SendManialinkPageAsync(BuildRoundsPromptManialink(info.NbLaps), cancellationToken: cancellationToken);
                }
                else
                {
                    // clear a leftover prompt from a previous multilap map - a new one only gets
                    // sent above when the CURRENT map is itself a multilap map
                    await HideManialinkAsync(RoundsPromptManialinkId, cancellationToken);
                }

                if (currentMapTrackId is { } trackIdForMeta)
                {
                    try
                    {
                        var meta = await tmxRules.GetTrackMetaAsync(trackIdForMeta, cancellationToken);
                        await client.SendManialinkPageAsync(BuildMapInfoManialink(meta.DifficultyLabel, meta.Awards, info.AuthorTime), cancellationToken: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: failed to fetch TMX map metadata - {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // an unhandled exception here would kill the callback dispatch loop entirely,
                // silently freezing every future event (joins, chat, finishes, the status page) -
                // every handler below is wrapped for the same reason
                Console.WriteLine($"Warning: checkpoint HUD setup failed - {ex.Message}");
            }
        });

        client.On("TrackMania.EndRace", async (methodParams, cancellationToken) =>
        {
            try
            {
                currentMap = null;
                currentMapTrackId = null;
                randomEnqueuedMapFileName = null;
                currentMapCheckpointTotal = null;
                playerCheckpointProgress.Clear();
                playerCheckpointTimestamp.Clear();
                // not hiding the manialink here: SendHideManialinkPage has no per-widget id to target
                // without also nuking the always-on top10 panel, and BeginRace overwrites the CP
                // counter text for the next map anyway, so the stale text is only visible for a moment
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: EndRace handling failed - {ex.Message}");
            }
        });

        client.On("TrackMania.PlayerCheckpoint", async (methodParams, cancellationToken) =>
        {
            try
            {
                var login = (string)methodParams[1];
                var checkpointIndex = (int)methodParams[4];

                if (currentMapCheckpointTotal is not { } total)
                {
                    return;
                }

                var current = checkpointIndex + 1;
                playerCheckpointProgress[login] = current;
                playerCheckpointTimestamp[login] = DateTimeOffset.UtcNow;

                await client.SendManialinkPageToLoginAsync(login, BuildCheckpointManialink(current, total), cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to update checkpoint HUD - {ex.Message}");
            }
        });

        client.On("TrackMania.PlayerConnect", async (methodParams, cancellationToken) =>
        {
            try
            {
                var login = (string)methodParams[0];

                nicknameCache[login] = await client.GetPlayerNicknameAsync(login, cancellationToken);

                await SendWelcomeMessageAsync(login, cancellationToken);
                await SendTop10PanelAsync(cancellationToken);
                await SendMapWidgetsToLoginAsync(login, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: PlayerConnect handling failed - {ex.Message}");
            }
        });

        client.On("TrackMania.PlayerChat", async (methodParams, cancellationToken) =>
        {
            try
            {
                var playerUid = (int)methodParams[0];
                var login = (string)methodParams[1];
                var message = (string)methodParams[2];
                var isRegisteredCmd = (bool)methodParams[3];

                if (isRegisteredCmd)
                {
                    await OnCommand(playerUid, login, message, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: chat command handling failed - {ex.Message}");
            }
        });

        client.On("TrackMania.PlayerFinish", async (methodParams, cancellationToken) =>
        {
            try
            {
                var playerUid = (int)methodParams[0];
                var login = (string)methodParams[1];
                var score = (int)methodParams[2];

                await OnPlayerFinish(playerUid, login, score, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: PlayerFinish handling failed - {ex.Message}");
            }
        });

        client.On("TrackMania.StatusChanged", async (methodParams, cancellationToken) =>
        {
            try
            {
                var statusCode = (TrackManiaStatusCode)(int)methodParams[0];

                if (SessionActive)
                {
                    switch (statusCode)
                    {
                        case TrackManiaStatusCode.Play:
                            sessionStopwatch?.Start();
                            break;
                        case TrackManiaStatusCode.Finish:
                            await FinishMapAsync(cancellationToken);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: StatusChanged handling failed - {ex.Message}");
            }
        });

        client.On("TrackMania.EndRound", async (methodParams, cancellationToken) =>
        {
            try
            {
                await FinishMapAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: EndRound handling failed - {ex.Message}");
            }
        });

        client.On("TrackMania.PlayerManialinkPageAnswer", async (methodParams, cancellationToken) =>
        {
            try
            {
                var playerUid = (int)methodParams[0];
                var login = (string)methodParams[1];
                var answer = (int)methodParams[2];
                await HandleManialinkAnswerAsync(playerUid, login, answer, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: manialink click handling failed - {ex.Message}");
            }
        });
    }

    // action IDs for clickable manialink widgets - sent back verbatim by the client as the
    // "Answer" param of TrackMania.PlayerManialinkPageAnswer when a player clicks a button
    private const int ActionVoteYes = 1;
    private const int ActionVoteNo = 2;
    private const int ActionRoundsAccept = 3;
    private const int ActionClosePresetList = 4;
    private const int ActionPresetBase = 10;

    private const string RoundsPromptManialinkId = "rounds_prompt";
    private const string VotePopupManialinkId = "vote_popup";
    private const string PresetListManialinkId = "preset_list";
    private const string MapInfoManialinkId = "map_info";

    // preset names as last shown to any player via the /presets widget, in click-index order -
    // clicking entry N in that widget re-runs it through the same path as /votepreset <name>
    private IReadOnlyList<string>? lastShownPresetNames;

    private async Task HandleManialinkAnswerAsync(int playerUid, string login, int answer, CancellationToken cancellationToken)
    {
        switch (answer)
        {
            case ActionVoteYes:
                await YesAsync(playerUid, login, [], cancellationToken);
                break;
            case ActionVoteNo:
                await NoAsync(playerUid, login, [], cancellationToken);
                break;
            case ActionRoundsAccept:
                await RoundsAsync(playerUid, login, [], cancellationToken);
                break;
            case ActionClosePresetList:
                await HideManialinkToLoginAsync(login, PresetListManialinkId, cancellationToken);
                break;
            default:
                var presetIndex = answer - ActionPresetBase;
                if (presetIndex >= 0 && lastShownPresetNames is { } names && presetIndex < names.Count)
                {
                    await VotePresetAsync(playerUid, login, [names[presetIndex]], cancellationToken);
                    await HideManialinkToLoginAsync(login, PresetListManialinkId, cancellationToken);
                }
                break;
        }
    }

    private async Task FinishMapAsync(CancellationToken cancellationToken)
    {
        // TODO: there should be some second tolerance
        var sessionExpired = config.TimeLimit.TotalMilliseconds > 0
            && sessionStopwatch is not null
            && sessionStopwatch.ElapsedMilliseconds - sessionStopwatchMillisecondOffset >= config.TimeLimit.TotalMilliseconds;

        // freeze time if it was still running
        if (sessionStopwatch?.IsRunning == true)
        {
            sessionStopwatch.Stop();

            if (!sessionExpired)
            {
                await SendFrozenTimeMessageAsync(cancellationToken);
            }
        }

        // if session expired, stop the session and reset the time limit
        if (sessionExpired)
        {
            await SendMessageAsync("$FF0Time limit reached! Stopping the session.", cancellationToken);
            await StopSessionAsync(cancellationToken);
        }
        else
        {
            await SetCalculatedTimeLimitAsync(cancellationToken);
        }
    }

    public async Task OnCommand(int playerUid, string login, string message, CancellationToken cancellationToken)
    {
        var trimmedMessage = message.TrimStart('/');
        var firstSpaceIndex = trimmedMessage.IndexOf(' ');
        var mainCommand = firstSpaceIndex == -1 ? trimmedMessage : trimmedMessage.Substring(0, firstSpaceIndex);

        if (commandHandlers.TryGetValue(mainCommand, out var handler))
        {
            var args = CommandArgsRegex().Matches(trimmedMessage)
                .Cast<Match>()
                .Skip(1)
                .Select(m => m.Value.Trim('"'))
                .ToArray();

            await handler(playerUid, login, args, cancellationToken);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        RegisterCallbacks();

        await SendWelcomeMessageAsync(login: null, cancellationToken);
        await SendTop10PanelAsync(cancellationToken);

        _ = StatusWriteLoopAsync(cancellationToken);

        if (config.AutoStart)
        {
            // the server may still be finishing its own startup map load (ServerSetup's warmup
            // challenge), so an immediate map change here can race it and throw "Change in progress"
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    await StartSessionAsync(cancellationToken);
                    break;
                }
                catch (Exception) when (attempt < 5)
                {
                    await Task.Delay(2000, cancellationToken);
                }
            }
        }

        await client.WaitForCloseAsync(cancellationToken);
    }

    private static readonly string statusFilePath = Path.Combine(AppContext.BaseDirectory, "WebStatus", "status.json");

    private async Task StatusWriteLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await WriteStatusAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to write status.json - {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }

    private async Task WriteStatusAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PlayerSummary> onlinePlayers = [];
        try
        {
            onlinePlayers = await client.GetPlayersAsync(cancellationToken);
        }
        catch (Exception)
        {
            // controller not fully connected yet, report nobody online for now
        }

        // same fallback as /map, /imp, /hard - see ImpossibleAsync for why
        var displayedTrackId = currentMapTrackId ?? pendingMapTrackId;

        string? mapName = null;
        int? nbCheckpoints = null;
        if (displayedTrackId is not null)
        {
            try
            {
                var info = await client.GetCurrentChallengeInfoAsync(cancellationToken);
                mapName = TmFormatCodeRegex().Replace(info.Name, string.Empty);
                nbCheckpoints = info.NbCheckpoints;
            }
            catch (Exception)
            {
                // map info not available yet
            }
        }

        // ranked by live race progress: most checkpoints first, ties broken by who reached
        // their current checkpoint first (see playerCheckpointTimestamp's declaration)
        var racers = onlinePlayers
            .Select(p => new
            {
                Nickname = TmFormatCodeRegex().Replace(p.NickName, string.Empty),
                Checkpoint = playerCheckpointProgress.GetValueOrDefault(p.Login, 0),
                Since = playerCheckpointTimestamp.GetValueOrDefault(p.Login, DateTimeOffset.MaxValue),
            })
            .OrderByDescending(p => p.Checkpoint)
            .ThenBy(p => p.Since)
            .Select((p, i) => new { p.Nickname, p.Checkpoint, IsLeader = i == 0 && p.Checkpoint > 0 })
            .ToList();

        var status = new
        {
            config.ServerName,
            SessionActive,
            PlayerCount = onlinePlayers.Count,
            PresetDisplayName = config.LastPreset?.DisplayName,
            CurrentMapName = mapName,
            CurrentMapCheckpoints = nbCheckpoints,
            CurrentMapTrackId = displayedTrackId,
            CurrentMapUrl = displayedTrackId is { } id ? $"https://{tmxRules.GetSiteUrl()}/trackshow/{id}" : null,
            CurrentMapImageUrl = displayedTrackId is { } imgId ? $"https://{tmxRules.GetSiteUrl()}/trackshow/{imgId}/image/1" : null,
            Players = racers,
            Top = leaderboard.GetTop(5),
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(statusFilePath)!);
        var json = JsonSerializer.Serialize(status);
        await File.WriteAllTextAsync(statusFilePath, json, cancellationToken);
    }

    private async Task StartAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (config.AdminLogins.Count > 0 && !config.AdminLogins.Contains(login))
        {
            await SendMessageAsync(login, "$F00Only server admins can start a session.", cancellationToken);
            return;
        }

        await StartSessionAsync(cancellationToken);
    }

    private async Task StartSessionAsync(CancellationToken cancellationToken)
    {
        if (!SessionActive)
        {
            sessionStopwatch = new();
            await SetTimeLimitAsync(cancellationToken);
            await SendMessageAsync([string.Empty, "$0F0Let's begin!"], cancellationToken);

            if (config.TimeLimit.TotalMilliseconds > 0)
            {
                await SendMessageAsync($"Time limit set to $FF0{new TimeSpan(config.TimeLimit.Ticks):g}", cancellationToken);
            }

            await NextRandomMapAsync(goalReached: false, cancellationToken);
        }
    }

    private async Task StopAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (config.AdminLogins.Count > 0 && !config.AdminLogins.Contains(login))
        {
            await SendMessageAsync(login, "$F00Only server admins can stop the session.", cancellationToken);
            return;
        }

        if (!SessionActive)
        {
            await SendMessageAsync(login, "$F00No active session to stop.", cancellationToken);
            return;
        }

        await StopSessionAsync(cancellationToken);

        if (await client.IsMultiplePlayersAsync(cancellationToken))
        {
            await SendMessageAsync($"$FF0Player {GetNicknameOrLogin(login)} has stopped the session!", cancellationToken);
        }
        else
        {
            await SendMessageAsync("$F00Session stopped!", cancellationToken);
        }
    }

    private async Task StopSessionAsync(CancellationToken cancellationToken)
    {
        sessionStopwatch?.Stop();
        sessionStopwatch = null;
        sessionStopwatchMillisecondOffset = 0;
        currentMap = null;
        currentMapTrackId = null;
        randomEnqueuedMapFileName = null;

        await client.CallAsync("SetTimeAttackLimit", [0], cancellationToken);
        await client.CallAsync("ChallengeRestart", [], cancellationToken);
    }

    private async Task SetTimeLimitAsync(CancellationToken cancellationToken)
    {
        await client.CallAsync("SetTimeAttackLimit", [config.TimeLimit.TotalMilliseconds], cancellationToken);
    }

    private async Task SetCalculatedTimeLimitAsync(CancellationToken cancellationToken)
    {
        if (config.TimeLimit.TotalMilliseconds <= 0 || sessionStopwatch is null)
        {
            return;
        }

        var elapsedMilliseconds = sessionStopwatch.ElapsedMilliseconds - sessionStopwatchMillisecondOffset;

        sessionStopwatchMillisecondOffset += 1500;

        await client.CallAsync("SetTimeAttackLimit", [config.TimeLimit.TotalMilliseconds - (int)elapsedMilliseconds], cancellationToken);
    }

    private async Task SkipAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (await client.IsMultiplePlayersAsync(cancellationToken))
        {
            await SendMessageAsync($"Player {GetNicknameOrLogin(login)} wants to skip the current challenge.", cancellationToken);
        }
        else
        {
            await SendMessageAsync("Skipping the current challenge...", cancellationToken);
        }

        await NextRandomMapAsync(goalReached: false, cancellationToken);
    }

    private async Task ImpossibleAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        // fall back to pendingMapTrackId - the map we most recently told the server to load - since
        // TrackMania.BeginRace (which confirms it into currentMapTrackId) can lag a few seconds
        // behind the actual challenge switch, and pendingMapTrackId never gets reset in the meantime
        if ((currentMapTrackId ?? pendingMapTrackId) is not { } trackId)
        {
            await SendMessageAsync(login, "$F00No map is currently loaded.", cancellationToken);
            return;
        }

        var tmxUrl = $"https://{tmxRules.GetSiteUrl()}/trackshow/{trackId}";

        await SendMessageAsync($"$F00Map {trackId} reported impossible by {GetNicknameOrLogin(login)} - pending review, skipping for now.", cancellationToken);
        await discordNotifier.PostAsync($"**{GetPlainNickname(login)}** reported map **{trackId}** as impossible: {tmxUrl}", cancellationToken);

        await NextRandomMapAsync(goalReached: false, cancellationToken);
    }

    private async Task HardAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if ((currentMapTrackId ?? pendingMapTrackId) is not { } trackId)
        {
            await SendMessageAsync(login, "$F00No map is currently loaded.", cancellationToken);
            return;
        }

        var tmxUrl = $"https://{tmxRules.GetSiteUrl()}/trackshow/{trackId}";

        await SendMessageAsync($"$FF0Map {trackId} flagged as hard by {GetNicknameOrLogin(login)} for review.", cancellationToken);
        await discordNotifier.PostHardAsync($"**{GetPlainNickname(login)}** flagged map **{trackId}** as hard: {tmxUrl}", cancellationToken);
    }

    private async Task TopAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        var top = leaderboard.GetTop(5);

        if (top.Count == 0)
        {
            await SendMessageAsync(login, "$F00No finishes recorded yet.", cancellationToken);
            return;
        }

        var lines = top.Select((entry, i) => $"{i + 1}. $FF0{entry.LastNickname}$FFF - {entry.Finishes} finish(es)");
        await SendMessageAsync(login, ["Top finishers on this server:", .. lines], cancellationToken);
    }

    private async Task RankAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        var rank = leaderboard.GetRank(login);

        if (rank is null)
        {
            await SendMessageAsync(login, "$F00You haven't finished a map on this server yet.", cancellationToken);
            return;
        }

        await SendMessageAsync(login, $"$0F0You are rank $FF0#{rank.Value.Position}$0F0 with $FF0{rank.Value.Finishes}$0F0 finish(es).", cancellationToken);
    }

    private async Task MapAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if ((currentMapTrackId ?? pendingMapTrackId) is not { } trackId)
        {
            await SendMessageAsync(login, "$F00No map is currently loaded.", cancellationToken);
            return;
        }

        // "http://" not "https://" - TMF's chat "$l[...]" link syntax prepends its own "http://"
        // on top of anything that isn't already that exact scheme, breaking the link (see /source)
        var tmxUrl = $"http://{tmxRules.GetSiteUrl()}/trackshow/{trackId}";
        var cpSuffix = string.Empty;

        try
        {
            var info = await client.GetCurrentChallengeInfoAsync(cancellationToken);
            if (info.NbCheckpoints is { } cpCount)
            {
                cpSuffix = $" $FFF({cpCount} CPs)";
            }
            if (info.LapRace)
            {
                cpSuffix += $" $F80[Multilap, {info.NbLaps} laps - $FFF/rounds$F80 for a valid replay]";
            }
        }
        catch (Exception)
        {
            // checkpoint count not available, just skip the suffix
        }

        await SendMessageAsync(login, $"$FF0Current map:{cpSuffix} $FFF$l[{tmxUrl}]{tmxUrl}$l", cancellationToken);
    }

    // switches the CURRENT map to Rounds mode so a multilap track can be finished properly - plain
    // TimeAttack lets a player cross the finish line once and be done, ignoring the map's real lap
    // count, which produces a replay TMX won't accept for a multilap track. Reverts to TimeAttack
    // automatically the moment the RMC moves on to its next map - see NextRandomMapAsync's
    // SetGameMode(1) call, which already runs unconditionally on every map fetch
    private async Task RoundsAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (!SessionActive)
        {
            await SendMessageAsync(login, "$F00No session is active.", cancellationToken);
            return;
        }

        ChallengeSummary info;
        try
        {
            info = await client.GetCurrentChallengeInfoAsync(cancellationToken);
        }
        catch (Exception)
        {
            await SendMessageAsync(login, "$F00Could not read the current map's info - try again in a moment.", cancellationToken);
            return;
        }

        if (!info.LapRace)
        {
            await SendMessageAsync(login, "$F00This map isn't a multilap track - no need for Rounds mode.", cancellationToken);
            return;
        }

        await client.CallAsync("SetGameMode", [0], cancellationToken); // 0 = Rounds
        await client.CallAsync("SetRoundForcedLaps", [0], cancellationToken); // 0 = use the map's own lap count
        await client.CallAsync("ChallengeRestart", [], cancellationToken);

        var lapsSuffix = info.NbLaps > 0 ? $" ({info.NbLaps} laps)" : string.Empty;
        await SendMessageAsync($"$0F0{GetNicknameOrLogin(login)} switched this multilap map to Rounds mode{lapsSuffix} for a valid replay. Back to TimeAttack once this map ends.", cancellationToken);
    }

    private async Task InfoAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        await SendMessageAsync(login, [
            "$0BF--- 100% TMX Project ---",
            "$FF0/skip$FFF - skip the current map (votes if multiple players are online)",
            "$FF0/map$FFF - show the current map's TMX link",
            "$FF0/rounds$FFF - switch a multilap map to Rounds mode for a valid replay",
            "$FF0/imp$FFF - report the current map as impossible for admin review",
            "$FF0/hard$FFF - flag the current map as hard for admin review",
            "$FF0/top$FFF - show the top finishers on this server",
            "$FF0/rank$FFF - show your own rank and finish count",
            "$FF0/votepreset <name>$FFF - propose switching preset, others confirm with $0F0/yes$FFF ($FF0/presets$FFF for names)",
            "$FF0/commands$FFF - list every raw command name",
            "$FF0/source$FFF - get the source code link for this modified server (AGPLv3)",
            "Admin-only: $FF0/start$FFF, $FF0/stop$FFF, $FF0/preset$FFF, $FF0/timelimit$FFF",
        ], cancellationToken);
    }

    private async Task TestHudAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        const string testXml = """
            <manialink version="1">
                <label posn="0 0 5" halign="center" valign="center" textsize="3" textcolor="FFFF" text="TEST HUD WORKS"/>
            </manialink>
            """;

        try
        {
            await client.SendManialinkPageToLoginAsync(login, testXml, cancellationToken: cancellationToken);
            await SendMessageAsync(login, "$0F0Test HUD sent - do you see big text in the middle of your screen?", cancellationToken);
        }
        catch (Exception ex)
        {
            await SendMessageAsync(login, $"$F00Failed to send: {ex.Message}", cancellationToken);
        }
    }

    private async Task CommandsAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        var commands = await client.GetChatCommandListAsync(cancellationToken);
        var formattedCommands = commands
            .Select(cmd => $"$FF0{cmd}$FFF")
            .Order();

        await SendMessageAsync(login, $"Commands: {string.Join(", ", formattedCommands)}", cancellationToken);
    }

    private async Task SourceAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        await SendMessageAsync(login, $"$FF0This server runs a modified, AGPLv3-licensed version of Randomizer Anywhere. Source: $FFF$l[{SourceRepoUrl}]{SourceRepoUrl}$l", cancellationToken);
    }

    private async Task TimeLimitAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            if (config.TimeLimit.TotalMilliseconds <= 0)
            {
                await SendMessageAsync(login, "Time limit is currently disabled. No time pressure!", cancellationToken);
            }
            else
            {
                await SendMessageAsync(login, $"Time limit is currently set to $FF0{new TimeSpan(config.TimeLimit.Ticks):g}", cancellationToken);
            }

            return;
        }

        var arg = args[0];

        if (arg.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            await SendMessageAsync(login, "Usage: $FF0/timelimit <seconds>", cancellationToken);
            return;
        }

        if (SessionActive)
        {
            await SendMessageAsync(login, "$F00Cannot change time limit while a session is active", cancellationToken);
            return;
        }

        if (!int.TryParse(arg, out var seconds) || seconds < 0)
        {
            await SendMessageAsync(login, $"$F00Invalid time limit value: {arg}. Please provide a non-negative integer.", cancellationToken);
            return;
        }

        config.TimeLimit = new TimeInt32(seconds * 1000);

        if (config.TimeLimit.TotalMilliseconds == 0)
        {
            if (await client.IsMultiplePlayersAsync(cancellationToken))
            {
                await SendMessageAsync($"Player {GetNicknameOrLogin(login)} has disabled the time limit.", cancellationToken);
            }
            else
            {
                await SendMessageAsync("Time limit disabled.", cancellationToken);
            }
        }
        else
        {
            if (await client.IsMultiplePlayersAsync(cancellationToken))
            {
                await SendMessageAsync($"Player {GetNicknameOrLogin(login)} has set the time limit to $FF0{new TimeSpan(config.TimeLimit.Ticks):g}", cancellationToken);
            }
            else
            {
                await SendMessageAsync($"Time limit set to $FF0{new TimeSpan(config.TimeLimit.Ticks):g}", cancellationToken);
            }
        }
    }

    private async Task PresetAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (config.AdminLogins.Count > 0 && !config.AdminLogins.Contains(login))
        {
            await SendMessageAsync(login, "$F00Only server admins can change the preset.", cancellationToken);
            return;
        }

        if (args.Length == 0)
        {
            var currentPresetMessage = string.IsNullOrWhiteSpace(config.LastPreset?.DisplayName)
                ? "No preset was yet applied."
                : $"Last preset: $FF0{config.LastPreset.DisplayName}";

            await SendMessageAsync(login, [currentPresetMessage, "Usage: $FF0/preset <name>"], cancellationToken);
            return;
        }

        var wasActive = SessionActive;
        if (wasActive)
        {
            ResetSessionStateForPresetSwitch();
        }

        var (success, displayName, error) = TryApplyPreset(args[0]);

        if (!success)
        {
            await SendMessageAsync(login, $"$F00{error}", cancellationToken);
            return;
        }

        if (await client.IsMultiplePlayersAsync(cancellationToken))
        {
            await SendMessageAsync($"Player {GetNicknameOrLogin(login)} has applied the $FF0{displayName}$FFF preset.", cancellationToken);
        }
        else
        {
            await SendMessageAsync($"$0F0Preset $FF0{displayName}$0F0 applied.", cancellationToken);
        }

        if (wasActive)
        {
            await StartSessionAsync(cancellationToken);
        }
    }

    // resets the local session bookkeeping a preset switch needs to clear, without the
    // ChallengeRestart RPC call StopSessionAsync makes - see FinalizeVoteAsync for why that
    // call must not race the NextChallenge call the caller is about to trigger via StartSessionAsync
    private void ResetSessionStateForPresetSwitch()
    {
        sessionStopwatch?.Stop();
        sessionStopwatch = null;
        sessionStopwatchMillisecondOffset = 0;
        currentMap = null;
        currentMapTrackId = null;
        randomEnqueuedMapFileName = null;
    }

    private (bool Success, string? DisplayName, string? Error) TryApplyPreset(string presetName)
    {
        var presetPath = Path.Combine(AppContext.BaseDirectory, "Presets", presetName + ".toml");

        if (!File.Exists(presetPath))
        {
            return (false, null, $"Preset '{presetName}' not found.");
        }

        var presetConfig = TomlLoader.LoadPresetConfig(presetPath);

        if (presetConfig is null)
        {
            return (false, null, $"Failed to load preset '{presetName}'.");
        }

        presetConfig.Apply(config);
        config.LastPreset = presetConfig;

        var displayName = string.IsNullOrWhiteSpace(presetConfig.DisplayName) ? presetName : presetConfig.DisplayName;
        return (true, displayName, null);
    }

    private async Task VotePresetAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            await SendMessageAsync(login, "Usage: $FF0/votepreset <name>$FFF, then others type $FF0/yes$FFF to support.", cancellationToken);
            return;
        }

        if (votePresetName is not null)
        {
            await SendMessageAsync(login, $"$F00A vote for '{votePresetName}' is already in progress.", cancellationToken);
            return;
        }

        var presetName = args[0];
        var presetPath = Path.Combine(AppContext.BaseDirectory, "Presets", presetName + ".toml");

        if (!File.Exists(presetPath))
        {
            await SendMessageAsync(login, $"$F00Preset '{presetName}' not found.", cancellationToken);
            return;
        }

        votePresetName = presetName;
        voteYesLogins = [login];

        await SendMessageAsync($"$FF0{GetNicknameOrLogin(login)} started a vote to switch to preset '{presetName}'. Type $0F0/yes$FF0 to support ({PresetVoteWindowMs / 1000}s window).", cancellationToken);
        await client.SendManialinkPageAsync(BuildVotePopupManialink(presetName), cancellationToken: cancellationToken);

        if (await HasVoteMajorityAsync(cancellationToken))
        {
            await FinalizeVoteAsync(cancellationToken);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PresetVoteWindowMs, cancellationToken);

                if (votePresetName != presetName)
                {
                    return; // already finalized or superseded
                }

                if (await HasVoteMajorityAsync(cancellationToken))
                {
                    await FinalizeVoteAsync(cancellationToken);
                }
                else
                {
                    await SendMessageAsync($"$F00Vote for preset '{presetName}' failed - not enough support.", cancellationToken);
                    votePresetName = null;
                    voteYesLogins = null;
                    await HideManialinkAsync(VotePopupManialinkId, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
            {
                // server shutting down mid-vote, nothing to do
            }
        }, cancellationToken);
    }

    private async Task YesAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (votePresetName is null || voteYesLogins is null)
        {
            await SendMessageAsync(login, "$F00No preset vote is currently active. Start one with $FF0/votepreset <name>$F00.", cancellationToken);
            return;
        }

        voteYesLogins.Add(login);

        if (await HasVoteMajorityAsync(cancellationToken))
        {
            await FinalizeVoteAsync(cancellationToken);
        }
    }

    private async Task NoAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (votePresetName is null || voteYesLogins is null)
        {
            await SendMessageAsync(login, "$F00No preset vote is currently active.", cancellationToken);
            return;
        }

        voteYesLogins.Remove(login);
        await SendMessageAsync(login, "Your vote against has been noted.", cancellationToken);
    }

    private async Task<bool> HasVoteMajorityAsync(CancellationToken cancellationToken)
    {
        var playerCount = await client.GetPlayerCountAsync(cancellationToken);
        return voteYesLogins is not null && voteYesLogins.Count * 2 > playerCount;
    }

    private async Task FinalizeVoteAsync(CancellationToken cancellationToken)
    {
        if (votePresetName is null)
        {
            return;
        }

        var presetName = votePresetName;
        votePresetName = null;
        voteYesLogins = null;
        await HideManialinkAsync(VotePopupManialinkId, cancellationToken);

        // see ResetSessionStateForPresetSwitch for why this isn't StopSessionAsync
        if (SessionActive)
        {
            ResetSessionStateForPresetSwitch();
        }

        var (success, displayName, error) = TryApplyPreset(presetName);

        if (!success)
        {
            await SendMessageAsync($"$F00Vote passed but preset failed to apply: {error}", cancellationToken);
            return;
        }

        await SendMessageAsync($"$0F0Vote passed! Preset $FF0{displayName}$0F0 applied.", cancellationToken);
        await StartSessionAsync(cancellationToken);
    }

    private async Task PresetsAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        var presetsDir = Path.Combine(AppContext.BaseDirectory, "Presets");

        if (!Directory.Exists(presetsDir))
        {
            await SendMessageAsync(login, "$F00No presets available.", cancellationToken);
            return;
        }

        var rawPresetNames = Directory.EnumerateFiles(presetsDir, "*.toml")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Order()
            .ToList();

        if (rawPresetNames.Count == 0)
        {
            await SendMessageAsync(login, "$F00No presets available.", cancellationToken);
            return;
        }

        var coloredNames = rawPresetNames.Select(name => $"$FF0{name}$FFF");
        await SendMessageAsync(login, [$"Presets: {string.Join(", ", coloredNames)}", "Select a preset using $FF0/preset <name>$FFF, or click below to start a vote"], cancellationToken);

        lastShownPresetNames = rawPresetNames;
        await client.SendManialinkPageToLoginAsync(login, BuildPresetListManialink(rawPresetNames), cancellationToken: cancellationToken);
    }

    public async Task OnPlayerFinish(int playerUid, string login, int score, CancellationToken cancellationToken)
    {
        if (!SessionActive)
        {
            return;
        }

        // see EnsureCurrentMapStateAsync's declaration - without this, a finish on a map that
        // loaded before anyone connected silently did nothing at all (no leaderboard entry, no
        // auto-skip, no replay link)
        await EnsureCurrentMapStateAsync(cancellationToken);

        if (currentMap is null)
        {
            return;
        }

        int? goalTime = config.AutoSkipMode switch
        {
            AutoSkipMode.AuthorMedal => currentMap.AuthorTime,
            AutoSkipMode.GoldMedal => currentMap.GoldTime,
            AutoSkipMode.SilverMedal => currentMap.SilverTime,
            AutoSkipMode.BronzeMedal => currentMap.BronzeTime,
            _ => null
        };

        if (score > 0 && (config.AutoSkipMode == AutoSkipMode.Finished || score <= goalTime))
        {
            var goalName = config.AutoSkipMode switch
            {
                AutoSkipMode.AuthorMedal => "Author Medal",
                AutoSkipMode.GoldMedal => "Gold Medal",
                AutoSkipMode.SilverMedal => "Silver Medal",
                AutoSkipMode.BronzeMedal => "Bronze Medal",
                _ => "finish line"
            };

            sessionStopwatch?.Stop();

            if (await client.IsMultiplePlayersAsync(cancellationToken))
            {
                await SendMessageAsync($"Player {GetNicknameOrLogin(login)} has reached the $FF0{goalName}$0F0!", cancellationToken);
            }
            else
            {
                await SendMessageAsync($"$0F0You have reached the $FF0{goalName}$0F0!", cancellationToken);
            }
            await SendFrozenTimeMessageAsync(cancellationToken);

            await leaderboard.RecordFinishAsync(login, GetPlainNickname(login), GetRawNickname(login), cancellationToken);
            await SendTop10PanelAsync(cancellationToken);
            await SendReplayLinkAsync(login, cancellationToken);
            await Task.Delay(ReplaySaveGraceMs, cancellationToken);

            await NextRandomMapAsync(goalReached: true, cancellationToken);
        }
    }

    private async Task SendReplayLinkAsync(string login, CancellationToken cancellationToken)
    {
        try
        {
            var fileName = $"{SanitizeFileNamePart(login)}_{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.Replay.Gbx";
            var relativeFileName = $"{config.ServerName}/{fileName}";

            await client.CallAsync("SaveBestGhostsReplay", [login, relativeFileName], cancellationToken);

            var host = string.IsNullOrWhiteSpace(config.PublicHost) ? "localhost" : config.PublicHost;
            var url = $"http://{host}:{config.ReplayServerPort}/replays/{Uri.EscapeDataString(fileName)}";

            await SendMessageAsync(login, $"$FF0Your TMX-valid replay: $FFF$l[{url}]{url}$l", cancellationToken);
        }
        catch (Exception ex)
        {
            await SendMessageAsync(login, "$F00Could not fetch your replay from the server, try saving manually with the in-game key.", cancellationToken);
            Console.WriteLine($"Warning: failed to send replay link to {login} - {ex.Message}");
        }
    }

    private static string SanitizeFileNamePart(string value)
    {
        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            buffer[i] = TmxRules.InvalidFileNameCharSearchValues.Contains(value[i]) ? '_' : value[i];
        }
        return new string(buffer);
    }

    public async Task NextRandomMapAsync(bool goalReached, CancellationToken cancellationToken)
    {
        // with multiple players, every finisher fires its own PlayerFinish -> NextRandomMapAsync
        // call; if one is already advancing the map, a second concurrent call must be a no-op,
        // not a second NextChallenge - see isAdvancingToNextMap's declaration for why
        if (isAdvancingToNextMap)
        {
            return;
        }

        isAdvancingToNextMap = true;
        try
        {
            // In case there are multiple players, the session stopwatch cannot be stopped immediately
            // so in case there is actually just one player, we need to account for the time it took to setup the next challenge
            var setupWatch = Stopwatch.StartNew();

            if (randomEnqueuedMapFileName is null)
            {
                var nextMap = await tmxRules.NextMapGbxAsync(cancellationToken);

                var mapPath = Path.Combine("_RandomizerAny", nextMap.FileName);
                await client.WriteFileAsync(mapPath, nextMap.Data, cancellationToken);
                await client.CallAsync("InsertChallenge", [mapPath], cancellationToken);
                await client.CallAsync("SetGameMode", [1], cancellationToken);

                randomEnqueuedMapFileName = mapPath;
                pendingMapTrackId = nextMap.TrackId;
            }

            if (await client.IsMultiplePlayersAsync(cancellationToken) && (!goalReached || config.CallVoteOnFinish))
            {
                await client.CallAsync("CallVote", [XmlRpcClient.GenerateXmlPayload("NextChallenge", [])], cancellationToken);
            }
            else
            {
                if (sessionStopwatch?.IsRunning == true)
                {
                    sessionStopwatchMillisecondOffset += (int)setupWatch.ElapsedMilliseconds;
                    sessionStopwatch.Stop();
                    await SendFrozenTimeMessageAsync(cancellationToken);
                }

                var info = await client.GetChallengeInfoAsync(randomEnqueuedMapFileName, cancellationToken);
                var cpSuffix = info.NbCheckpoints is { } cpCount ? $" ({cpCount} CPs)" : string.Empty;
                await SendMessageAsync($"Next map is ready: {info.Name}{cpSuffix}", cancellationToken);
                await client.CallAsync("NextChallenge", [], cancellationToken);
            }
        }
        finally
        {
            isAdvancingToNextMap = false;
        }
    }

    private async Task SendWelcomeMessageAsync(string? login, CancellationToken cancellationToken)
    {
        await SendMessageAsync(login, config.WelcomeMessage.Prepend(string.Empty), cancellationToken);
    }

    private string GetServerMessageType(string? login)
    {
        if (false)
        {
            return login is null ? "ChatSend" : "ChatSendToLogin";
        }
        else
        {
            return login is null ? "ChatSendServerMessage" : "ChatSendServerMessageToLogin";
        }
    }

    private async Task SendMessageAsync(string? login, string message, CancellationToken cancellationToken)
    {
        await client.CallAsync(GetServerMessageType(login), login is null ? [message] : [message, login], cancellationToken);
    }

    private async Task SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        await SendMessageAsync(login: null, message, cancellationToken);
    }

    private async Task SendMessageAsync(string? login, IEnumerable<string> messageLines, CancellationToken cancellationToken)
    {
        var serverMessageType = GetServerMessageType(login);

        await client.SystemMulticallAsync(messageLines
            .Select(msg => new XmlRpcMulticall(serverMessageType, login is null ? [msg] : [msg, login])), cancellationToken);
    }

    private async Task SendMessageAsync(IEnumerable<string> messageLines, CancellationToken cancellationToken)
    {
        await SendMessageAsync(login: null, messageLines, cancellationToken);
    }

    private async Task SendFrozenTimeMessageAsync(CancellationToken cancellationToken)
    {
        if (config.TimeLimit.TotalMilliseconds <= 0 || sessionStopwatch is null)
        {
            return;
        }

        var millisecondsLeft = config.TimeLimit.TotalMilliseconds - (sessionStopwatch.ElapsedMilliseconds - sessionStopwatchMillisecondOffset);

        await SendMessageAsync($"Time limit frozen at $FF0{TimeSpan.FromMilliseconds(millisecondsLeft):g}", cancellationToken);
    }

    private string GetNicknameOrLogin(string login)
    {
        return nicknameCache.TryGetValue(login, out var nickname) ? $"$<{nickname}$>" : login;
    }

    private async Task SendTop10PanelAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.SendManialinkPageAsync(BuildTop10Manialink(leaderboard.GetTop(10)), cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: failed to update top 10 panel - {ex.Message}");
        }
    }

    // "SendHideManialinkPageToId" (RemoteClient used to have a wrapper for this) actually expects
    // a numeric UId, not our string XML "id" attribute, and throws "Value of type STRING supplied
    // where type INT was expected" - the real way to clear a specific id'd manialink is to send a
    // new, empty <manialink> with that same id (documented TMF behavior: a previously displayed
    // manialink with a matching id gets deleted when the replacement has no content)
    private async Task HideManialinkAsync(string id, CancellationToken cancellationToken)
    {
        await client.SendManialinkPageAsync($"""<manialink id="{id}" version="1"></manialink>""", cancellationToken: cancellationToken);
    }

    // same empty-body-replace trick as HideManialinkAsync, but for a manialink that was only ever
    // sent to one login (SendManialinkPageToLoginAsync) rather than broadcast to everyone
    private async Task HideManialinkToLoginAsync(string login, string id, CancellationToken cancellationToken)
    {
        await client.SendManialinkPageToLoginAsync(login, $"""<manialink id="{id}" version="1"></manialink>""", cancellationToken: cancellationToken);
    }

    // BeginRace can fail to fire for a map that was already loaded before any player connected
    // (confirmed live: no widgets appeared until the first skip/finish, and a finish on that
    // first map didn't record or auto-skip at all) - this fills in currentMap/
    // currentMapCheckpointTotal/currentMapTrackId on demand from the actually-running challenge,
    // so OnPlayerFinish and the widget senders below never silently no-op because BeginRace never
    // got the chance to populate them
    private async Task<ChallengeSummary> EnsureCurrentMapStateAsync(CancellationToken cancellationToken)
    {
        var info = await client.GetCurrentChallengeInfoAsync(cancellationToken);

        currentMap ??= new MapInfo(
            AuthorTime: info.AuthorTime,
            GoldTime: info.GoldTime,
            SilverTime: info.SilverTime,
            BronzeTime: info.BronzeTime
        );
        currentMapCheckpointTotal ??= info.NbCheckpoints;
        currentMapTrackId ??= pendingMapTrackId;

        return info;
    }

    // sends the same set of map widgets BeginRace normally broadcasts to everyone, but to a
    // single just-connected player - needed both for a genuinely late joiner and for the
    // BeginRace-never-fired case EnsureCurrentMapStateAsync recovers from
    private async Task SendMapWidgetsToLoginAsync(string login, CancellationToken cancellationToken)
    {
        var info = await EnsureCurrentMapStateAsync(cancellationToken);

        if (currentMapCheckpointTotal is { } total)
        {
            var current = playerCheckpointProgress.GetValueOrDefault(login, 0);
            await client.SendManialinkPageToLoginAsync(login, BuildCheckpointManialink(current, total), cancellationToken: cancellationToken);
        }

        if (info.LapRace)
        {
            await client.SendManialinkPageToLoginAsync(login, BuildRoundsPromptManialink(info.NbLaps), cancellationToken: cancellationToken);
        }

        if (currentMapTrackId is { } trackId)
        {
            try
            {
                var meta = await tmxRules.GetTrackMetaAsync(trackId, cancellationToken);
                await client.SendManialinkPageToLoginAsync(login, BuildMapInfoManialink(meta.DifficultyLabel, meta.Awards, info.AuthorTime), cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to fetch TMX map metadata for {login} - {ex.Message}");
            }
        }
    }

    private static string BuildCheckpointManialink(int current, int total) => $"""
        <manialink id="cp_counter" version="1">
            <quad posn="-64 37 4" sizen="13 5" halign="left" valign="center" bgcolor="000A"/>
            <label posn="-63 37 5" halign="left" valign="center" textsize="2.5" textcolor="FFFF" text="CP {current} / {total}"/>
        </manialink>
        """;

    // sits just above the CP counter (which is anchored at y=37) so the two never overlap
    private static string BuildMapInfoManialink(string difficultyLabel, int awards, int authorTimeMs)
    {
        var difficultyColor = difficultyLabel switch
        {
            "Beginner" => "0F0",
            "Intermediate" => "0AF",
            "Expert" => "FA0",
            "Lunatic" => "F0F",
            _ => "FFF",
        };

        // "$o" for bold, then "$z$FFF" to fully reset before the awards count so the bold/color
        // doesn't bleed into it - same reset pattern as the top-10 panel, see its own note on why
        // "$<...$>" isn't used instead
        var awardsSuffix = System.Security.SecurityElement.Escape($" · {awards} award{(awards == 1 ? "" : "s")}");
        var difficultyText = System.Security.SecurityElement.Escape(difficultyLabel);
        var authorTimeText = System.Security.SecurityElement.Escape($"AT {new TimeInt32(authorTimeMs)}");

        return $"""
            <manialink id="{MapInfoManialinkId}" version="1">
                <quad posn="-64 43 4" sizen="20 7" halign="left" valign="center" bgcolor="000A"/>
                <label posn="-63 45 5" halign="left" valign="center" textsize="1.3" textcolor="{difficultyColor}F" text="$o{difficultyText}$z$FFF{awardsSuffix}"/>
                <label posn="-63 41.5 5" halign="left" valign="center" textsize="1.3" textcolor="0F0F" text="$o{authorTimeText}"/>
            </manialink>
            """;
    }

    // sits just below the CP counter - only sent while the loaded map is a real multilap
    // challenge (see BeginRace), hidden again the moment a non-multilap map loads
    private static string BuildRoundsPromptManialink(int nbLaps) => $"""
        <manialink id="{RoundsPromptManialinkId}" version="1">
            <quad posn="-64 22 4" sizen="20 6.5" halign="left" valign="center" bgcolor="000A"/>
            <label posn="-63 24.3 5" halign="left" valign="center" textsize="1.1" textcolor="FF8" text="Multilap map ({nbLaps} laps)"/>
            <quad posn="-63 22.3 5" sizen="18 2.6" halign="left" valign="center" bgcolor="0B3A" action="{ActionRoundsAccept}"/>
            <label posn="-62 22.3 6" halign="left" valign="center" textsize="1" textcolor="FFF" text="Click for Rounds mode"/>
        </manialink>
        """;

    private static string BuildVotePopupManialink(string presetName)
    {
        var text = System.Security.SecurityElement.Escape($"Switch preset to '{presetName}'?");
        return $"""
            <manialink id="{VotePopupManialinkId}" version="1">
                <quad posn="-25 46 4" sizen="50 10" halign="left" valign="top" bgcolor="000C"/>
                <label posn="0 44 5" halign="center" valign="center" textsize="1.5" textcolor="FF0F" text="{text}"/>
                <quad posn="-15 40 5" sizen="12 3.5" halign="center" valign="center" bgcolor="0B3A" action="{ActionVoteYes}"/>
                <label posn="-15 40 6" halign="center" valign="center" textsize="1.2" textcolor="FFF" text="YES"/>
                <quad posn="15 40 5" sizen="12 3.5" halign="center" valign="center" bgcolor="B00A" action="{ActionVoteNo}"/>
                <label posn="15 40 6" halign="center" valign="center" textsize="1.2" textcolor="FFF" text="NO"/>
            </manialink>
            """;
    }

    private static string BuildPresetListManialink(IReadOnlyList<string> presetNames)
    {
        // manialink coordinates must use "." as the decimal separator regardless of the host
        // machine's locale - plain string interpolation of a double formats it with the CURRENT
        // THREAD CULTURE, which on a German-locale box turns e.g. "18.5" into "18,5" and breaks
        // the XML attribute (extra token where the parser expects exactly one number pair)
        static string Inv(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var rows = new System.Text.StringBuilder();
        var y = 18.0;

        for (var i = 0; i < presetNames.Count; i++)
        {
            var text = System.Security.SecurityElement.Escape(presetNames[i]);
            rows.AppendLine($"""<quad posn="-19 {Inv(y)} 5" sizen="38 3" halign="left" valign="center" bgcolor="0004" action="{ActionPresetBase + i}"/>""");
            rows.AppendLine($"""<label posn="0 {Inv(y)} 6" halign="center" valign="center" textsize="1.2" textcolor="FFF" text="{text}"/>""");
            y -= 3.5;
        }

        var boxHeight = 10 + (presetNames.Count * 3.5);

        return $"""
            <manialink id="{PresetListManialinkId}" version="1">
                <quad posn="-20 23 4" sizen="40 {Inv(boxHeight)}" halign="left" valign="top" bgcolor="000C"/>
                <label posn="0 21.5 5" halign="center" valign="center" textsize="1.5" textcolor="FF0F" text="Click to vote for a preset"/>
                <quad posn="18.5 21.7 5" sizen="3 3" halign="center" valign="center" bgcolor="B00A" action="{ActionClosePresetList}"/>
                <label posn="18.5 21.7 6" halign="center" valign="center" textsize="1.1" textcolor="FFF" text="X"/>
                {rows}
            </manialink>
            """;
    }

    // a flat character count doesn't track rendered width - a lot of TMF nicknames lean on wide
    // Unicode lookalike glyphs (Cyrillic/Greek/symbols) that render noticeably wider than plain
    // ASCII in-game, so those need to cost more against the budget than a raw count would suggest
    private const double Top10NicknameWidthBudget = 13.0;
    private const double WideCharWidth = 1.8;

    // truncates by visible width only - $-format codes are zero-width and are always consumed
    // whole (never split), so a truncated name keeps working, valid color codes instead of
    // falling back to plain text
    private static string TruncateForDisplayWidth(string nickname)
    {
        var width = 0.0;
        var i = 0;

        while (i < nickname.Length)
        {
            if (nickname[i] == '$')
            {
                var codeMatch = TmFormatCodeRegex().Match(nickname, i);
                if (codeMatch.Success && codeMatch.Index == i)
                {
                    i += codeMatch.Length;
                    continue;
                }
            }

            var charWidth = nickname[i] <= 0x7F ? 1.0 : WideCharWidth;
            if (width + charWidth > Top10NicknameWidthBudget)
            {
                return nickname[..i] + "…";
            }

            width += charWidth;
            i++;
        }

        return nickname;
    }

    private static string BuildTop10Manialink(IReadOnlyList<LeaderboardEntry> top)
    {
        var rows = new System.Text.StringBuilder();
        var y = 16;

        rows.AppendLine("""<label posn="62 18 5" halign="right" valign="center" textsize="1.9" textcolor="FF0F" text="$s$oTop Finishers"/>""");

        for (var i = 0; i < top.Count; i++)
        {
            var entry = top[i];

            // truncation is code-aware (see TruncateForDisplayWidth), so even a cut-off name keeps
            // its real in-game color. Reset with "$z" rather than wrapping in "$<...$>" - TMF's
            // manialink parser isn't standards-compliant XML, so a literal "<"/">" here would get
            // entity-escaped below and might not get decoded back by the game
            var source = string.IsNullOrEmpty(entry.RawNickname) ? entry.LastNickname : entry.RawNickname;
            var displayName = TruncateForDisplayWidth(source);

            var text = System.Security.SecurityElement.Escape($"{i + 1}. {displayName} $z$FFF- {entry.Finishes}");
            y -= 4;
            rows.AppendLine($"""<label posn="62 {y} 5" halign="right" valign="center" textsize="1.2" textcolor="FFFF" text="{text}"/>""");
        }

        var boxHeight = 10 + (top.Count * 4) + 4;

        return $"""
            <manialink id="top10_panel" version="1">
                <quad posn="64 20 4" sizen="14 {boxHeight}" halign="right" valign="top" bgcolor="000A"/>
                {rows}
            </manialink>
            """;
    }

    // plain-text nickname for destinations that don't understand TM's $-formatting codes (e.g. Discord)
    private string GetPlainNickname(string login)
    {
        var nickname = nicknameCache.TryGetValue(login, out var nick) ? nick : login;
        return TmFormatCodeRegex().Replace(nickname, string.Empty);
    }

    // nickname with its original $-formatting codes intact, for HUD/manialink text (which renders
    // them the same way in-game chat does) - not safe for Discord/the status page, see GetPlainNickname
    private string GetRawNickname(string login)
    {
        return nicknameCache.TryGetValue(login, out var nick) ? nick : login;
    }

    [GeneratedRegex(@"[^\s""]+|""[^""]*""")]
    private static partial Regex CommandArgsRegex();

    [GeneratedRegex(@"\$([0-9a-fA-F]{3}|[<>oiswnmgzt$])")]
    private static partial Regex TmFormatCodeRegex();
}
