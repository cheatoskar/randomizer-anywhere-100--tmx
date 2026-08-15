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
            ["info"] = InfoAsync,
            ["votepreset"] = VotePresetAsync,
            ["yes"] = YesAsync,
            ["no"] = NoAsync,
            ["commands"] = CommandsAsync,
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
            var mapInfo = (Dictionary<string, object>)methodParams[0];

            currentMap = new MapInfo(
                AuthorTime: (int)mapInfo["AuthorTime"],
                GoldTime: (int)mapInfo["GoldTime"],
                SilverTime: (int)mapInfo["SilverTime"],
                BronzeTime: (int)mapInfo["BronzeTime"]
            );
            currentMapTrackId = pendingMapTrackId;
        });

        client.On("TrackMania.EndRace", async (methodParams, cancellationToken) =>
        {
            currentMap = null;
            currentMapTrackId = null;
            randomEnqueuedMapFileName = null;
        });

        client.On("TrackMania.PlayerConnect", async (methodParams, cancellationToken) =>
        {
            var login = (string)methodParams[0];

            nicknameCache[login] = await client.GetPlayerNicknameAsync(login, cancellationToken);

            await SendWelcomeMessageAsync(login, cancellationToken);
        });

        client.On("TrackMania.PlayerChat", async (methodParams, cancellationToken) =>
        {
            var playerUid = (int)methodParams[0];
            var login = (string)methodParams[1];
            var message = (string)methodParams[2];
            var isRegisteredCmd = (bool)methodParams[3];

            if (isRegisteredCmd)
            {
                await OnCommand(playerUid, login, message, cancellationToken);
            }
        });

        client.On("TrackMania.PlayerFinish", async (methodParams, cancellationToken) =>
        {
            var playerUid = (int)methodParams[0];
            var login = (string)methodParams[1];
            var score = (int)methodParams[2];

            await OnPlayerFinish(playerUid, login, score, cancellationToken);
        });

        client.On("TrackMania.StatusChanged", async (methodParams, cancellationToken) =>
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
        });

        client.On("TrackMania.EndRound", async (methodParams, cancellationToken) =>
        {
            await FinishMapAsync(cancellationToken);
        });
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
        var playerCount = 0;
        try
        {
            playerCount = await client.GetPlayerCountAsync(cancellationToken);
        }
        catch (Exception)
        {
            // controller not fully connected yet, report 0 for now
        }

        string? mapName = null;
        if (randomEnqueuedMapFileName is not null)
        {
            try
            {
                mapName = TmFormatCodeRegex().Replace(await client.GetMapNameAsync(randomEnqueuedMapFileName, cancellationToken), string.Empty);
            }
            catch (Exception)
            {
                // map info not available yet
            }
        }

        var status = new
        {
            config.ServerName,
            SessionActive,
            PlayerCount = playerCount,
            CurrentMapName = mapName,
            CurrentMapTrackId = currentMapTrackId,
            CurrentMapUrl = currentMapTrackId is { } id ? $"https://{tmxRules.GetSiteUrl()}/trackshow/{id}" : null,
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
        if (currentMapTrackId is not { } trackId)
        {
            await SendMessageAsync(login, "$F00No map is currently loaded.", cancellationToken);
            return;
        }

        var tmxUrl = $"https://{tmxRules.GetSiteUrl()}/trackshow/{trackId}";

        await SendMessageAsync($"$F00Map {trackId} reported impossible by {GetNicknameOrLogin(login)} - pending review, skipping for now.", cancellationToken);
        await discordNotifier.PostAsync($"🚫 **{GetPlainNickname(login)}** reported map **{trackId}** as impossible: {tmxUrl}", cancellationToken);

        await NextRandomMapAsync(goalReached: false, cancellationToken);
    }

    private async Task HardAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        if (currentMapTrackId is not { } trackId)
        {
            await SendMessageAsync(login, "$F00No map is currently loaded.", cancellationToken);
            return;
        }

        var tmxUrl = $"https://{tmxRules.GetSiteUrl()}/trackshow/{trackId}";

        await SendMessageAsync($"$FF0Map {trackId} flagged as hard by {GetNicknameOrLogin(login)} for review.", cancellationToken);
        await discordNotifier.PostHardAsync($"⚠️ **{GetPlainNickname(login)}** flagged map **{trackId}** as hard: {tmxUrl}", cancellationToken);
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
        if (currentMapTrackId is not { } trackId)
        {
            await SendMessageAsync(login, "$F00No map is currently loaded.", cancellationToken);
            return;
        }

        var tmxUrl = $"https://{tmxRules.GetSiteUrl()}/trackshow/{trackId}";
        await SendMessageAsync(login, $"$FF0Current map: $FFF$l[{tmxUrl}]{tmxUrl}$l", cancellationToken);
    }

    private async Task InfoAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        await SendMessageAsync(login, [
            "$0BF--- 100% TMX Project ---",
            "$FF0/skip$FFF - skip the current map (votes if multiple players are online)",
            "$FF0/map$FFF - show the current map's TMX link",
            "$FF0/imp$FFF - report the current map as impossible for admin review",
            "$FF0/hard$FFF - flag the current map as hard for admin review",
            "$FF0/top$FFF - show the top finishers on this server",
            "$FF0/rank$FFF - show your own rank and finish count",
            "$FF0/votepreset <name>$FFF - propose switching preset, others confirm with $0F0/yes$FFF ($FF0/presets$FFF for names)",
            "$FF0/commands$FFF - list every raw command name",
            "Admin-only: $FF0/start$FFF, $FF0/stop$FFF, $FF0/preset$FFF, $FF0/timelimit$FFF",
        ], cancellationToken);
    }

    private async Task CommandsAsync(int playerUid, string login, string[] args, CancellationToken cancellationToken)
    {
        var commands = await client.GetChatCommandListAsync(cancellationToken);
        var formattedCommands = commands
            .Select(cmd => $"$FF0{cmd}$FFF")
            .Order();

        await SendMessageAsync(login, $"Commands: {string.Join(", ", formattedCommands)}", cancellationToken);
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

        if (SessionActive)
        {
            await SendMessageAsync(login, "$F00Cannot change preset while a session is active", cancellationToken);
            return;
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

        if (SessionActive)
        {
            await StopSessionAsync(cancellationToken);
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

        var presetNames = Directory.EnumerateFiles(presetsDir, "*.toml")
            .Select(path => $"$FF0{Path.GetFileNameWithoutExtension(path)}$FFF")
            .Order()
            .ToList();

        if (presetNames.Count == 0)
        {
            await SendMessageAsync(login, "$F00No presets available.", cancellationToken);
            return;
        }

        await SendMessageAsync(login, [$"Presets: {string.Join(", ", presetNames)}", "Select a preset using $FF0/preset <name>$FFF"], cancellationToken);
    }

    public async Task OnPlayerFinish(int playerUid, string login, int score, CancellationToken cancellationToken)
    {
        if (!SessionActive)
        {
            return;
        }

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

            await leaderboard.RecordFinishAsync(login, GetPlainNickname(login), cancellationToken);
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

            var mapName = await client.GetMapNameAsync(randomEnqueuedMapFileName, cancellationToken);
            await SendMessageAsync($"Next map is ready: {mapName}", cancellationToken);
            await client.CallAsync("NextChallenge", [], cancellationToken);
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

    // plain-text nickname for destinations that don't understand TM's $-formatting codes (e.g. Discord)
    private string GetPlainNickname(string login)
    {
        var nickname = nicknameCache.TryGetValue(login, out var nick) ? nick : login;
        return TmFormatCodeRegex().Replace(nickname, string.Empty);
    }

    [GeneratedRegex(@"[^\s""]+|""[^""]*""")]
    private static partial Regex CommandArgsRegex();

    [GeneratedRegex(@"\$([0-9a-fA-F]{3}|[<>oiswnmgzt$])")]
    private static partial Regex TmFormatCodeRegex();
}
