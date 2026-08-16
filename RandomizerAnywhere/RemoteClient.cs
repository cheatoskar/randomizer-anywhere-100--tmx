using ManiaAPI.XmlRpc;
using Polly;
using Polly.Retry;
using RandomizerAnywhere.Config;
using System.Globalization;
using System.Net;
using System.Linq;

namespace RandomizerAnywhere;

internal sealed class RemoteClient : IAsyncDisposable, IDisposable
{
    private static readonly ResiliencePipeline connectionPipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 5,
            BackoffType = DelayBackoffType.Exponential
        })
        .Build();

    private static readonly string[] buildFormats = [
        "yyyy-MM-dd_HH_mm",
        "yyyy-MM-dd"
    ];

    private readonly AppConfig config;

    private XmlRpcClient? raw;

    public XmlRpcClient Raw => raw ?? throw new InvalidOperationException("Client is not connected.");

    private HashSet<string>? supportedMethods;
    private HashSet<string> SupportedMethods => supportedMethods ?? throw new InvalidOperationException("Client is not connected.");

    private RemoteVersion? versionInfo;
    public RemoteVersion VersionInfo => versionInfo ?? throw new InvalidOperationException("Version info is not available. Call GetVersionAsync first.");

    public RemoteClient(AppConfig config)
    {
        this.config = config;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        raw = await connectionPipeline.ExecuteAsync(async token =>
            await XmlRpcClient.ConnectAsync(IPAddress.Loopback, config.XmlRpcPort, cancellationToken: token), cancellationToken);

        supportedMethods = new(await raw.SystemListMethodsAsync(cancellationToken));
    }

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        var result = await Raw.CallAsync<bool>("Authenticate", ["SuperAdmin", "SuperAdmin"], cancellationToken);

        if (!result)
        {
            throw new InvalidOperationException("Failed to authenticate as SuperAdmin.");
        }
    }

    public async Task SetServerNameAsync(string serverName, CancellationToken cancellationToken = default)
    {
        var result = await Raw.CallAsync<bool>("SetServerName", [serverName], cancellationToken);

        if (!result)
        {
            throw new InvalidOperationException("Failed to set server name.");
        }
    }

    public async ValueTask<RemoteVersion> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        if (versionInfo is not null)
        {
            return versionInfo;
        }

        var versionDict = await Raw.CallAsync<Dictionary<string, object>>("GetVersion", [], cancellationToken);

        var buildString = versionDict.TryGetValue("Build", out var build) ? build as string : null;
        var buildDate = buildString is null ? default : DateTime.TryParseExact(buildString, buildFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedBuild) ? parsedBuild : default(DateTime?);

        return versionInfo = new RemoteVersion(
            versionDict["Name"] as string ?? throw new InvalidOperationException("Missing Name in GetVersion response"),
            versionDict["Version"] as string ?? throw new InvalidOperationException("Missing Version in GetVersion response"),
            buildDate,
            versionDict.TryGetValue("TitleId", out var titleId) ? titleId as string : null);
    }

    public async Task<bool> EnableCallbacksAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportedMethods.Contains("EnableCallbacks"))
        {
            return false;
        }

        var result = await Raw.CallAsync<bool>("EnableCallbacks", [true], cancellationToken);

        if (!result)
        {
            throw new InvalidOperationException("Failed to enable callbacks.");
        }

        return result;
    }

    public bool SupportsWriteFile() => SupportedMethods.Contains("WriteFile");

    public async Task WriteFileAsync(string filePath, byte[] fileData, CancellationToken cancellationToken = default)
    {
        if (SupportedMethods.Contains("WriteFile"))
        {
            await CallAsync("WriteFile", [filePath, fileData], cancellationToken);
            return;
        }

        var tracksDirectory = await Raw.CallAsync<string>("GetTracksDirectory", [], cancellationToken);

        if (Path.GetDirectoryName(filePath) is string directoryRelativePath)
        {
            Directory.CreateDirectory(Path.Combine(tracksDirectory, directoryRelativePath));
        }

        await File.WriteAllBytesAsync(Path.Combine(tracksDirectory, filePath), fileData, cancellationToken);
    }

    public async Task CallAsync(string methodName, object[] parameters, CancellationToken cancellationToken = default)
    {
        var result = await Raw.CallAsync(methodName, parameters, cancellationToken);

        if (result is false)
        {
            throw new InvalidOperationException($"Failed to call method {methodName}.");
        }
    }

    public async Task<IEnumerable<XmlRpcMulticallResult>> SystemMulticallAsync(IEnumerable<XmlRpcMulticall> calls, CancellationToken cancellationToken = default)
    {
        return await Raw.SystemMulticallAsync(calls, cancellationToken);
    }

    public async Task WaitForCloseAsync(CancellationToken cancellationToken)
    {
        await Raw.WaitForCloseAsync(cancellationToken);
    }

    public async Task<string> GetPlayerNicknameAsync(string login, CancellationToken cancellationToken = default)
    {
        var playerInfo = await Raw.CallAsync<Dictionary<string, object>>("GetPlayerInfo", [login], cancellationToken);
        return (string)playerInfo["NickName"];
    }

    public async Task<IEnumerable<string>> GetChatCommandListAsync(CancellationToken cancellationToken = default)
    {
        var commandList = await Raw.CallAsync<List<object>>("GetChatCommandList", [(int)short.MaxValue, 0], cancellationToken);
        return commandList.OfType<IReadOnlyDictionary<string, object>>()
            .Select(x => (string)x["Name"]);
    }

    public async Task<bool> IsMultiplePlayersAsync(CancellationToken cancellationToken = default)
    {
        var playerCount = await GetPlayerCountAsync(cancellationToken);
        return playerCount > 1;
    }

    public async Task<int> GetPlayerCountAsync(CancellationToken cancellationToken = default)
    {
        var players = await GetPlayersAsync(cancellationToken);
        return players.Count;
    }

    // maxCount of 200 comfortably covers TMF's real player cap (well under 100) - GetPlayerCountAsync
    // used to pass 2 here, which silently capped the reported count at 2 once a 3rd player joined
    public async Task<IReadOnlyList<PlayerSummary>> GetPlayersAsync(CancellationToken cancellationToken = default)
    {
        var playerList = await Raw.CallAsync<List<object>>("GetPlayerList", [200, 0], cancellationToken);
        return playerList.OfType<Dictionary<string, object>>()
            .Where(p => p.TryGetValue("Login", out var login) && !string.IsNullOrEmpty(login as string))
            .Select(p => new PlayerSummary((string)p["Login"], (string)p["NickName"]))
            .ToList();
    }

    public async Task<ChallengeSummary> GetChallengeInfoAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var mapInfo = await Raw.CallAsync<Dictionary<string, object>>("GetChallengeInfo", [fileName], cancellationToken);
        return ToChallengeSummary(mapInfo);
    }

    // unlike GetChallengeInfo(fileName), which can report placeholder values (e.g. NbCheckpoints = -1)
    // for a challenge that's only queued and not yet actively loaded, this reflects the map that's
    // actually running right now
    public async Task<ChallengeSummary> GetCurrentChallengeInfoAsync(CancellationToken cancellationToken = default)
    {
        var mapInfo = await Raw.CallAsync<Dictionary<string, object>>("GetCurrentChallengeInfo", [], cancellationToken);
        return ToChallengeSummary(mapInfo);
    }

    public async Task SendManialinkPageAsync(string manialink, int timeoutSeconds = 0, bool hideOnClick = false, CancellationToken cancellationToken = default)
    {
        await Raw.CallAsync("SendDisplayManialinkPage", [manialink, timeoutSeconds, hideOnClick], cancellationToken);
    }

    public async Task SendManialinkPageToLoginAsync(string login, string manialink, int timeoutSeconds = 0, bool hideOnClick = false, CancellationToken cancellationToken = default)
    {
        await Raw.CallAsync("SendDisplayManialinkPageToLogin", [login, manialink, timeoutSeconds, hideOnClick], cancellationToken);
    }

    public async Task HideManialinkPageByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await Raw.CallAsync("SendHideManialinkPageToId", [id], cancellationToken);
    }

    public async Task HideAllManialinksAsync(CancellationToken cancellationToken = default)
    {
        await Raw.CallAsync("SendHideManialinkPage", [], cancellationToken);
    }

    private static ChallengeSummary ToChallengeSummary(Dictionary<string, object> mapInfo)
    {
        var nbCheckpoints = mapInfo.TryGetValue("NbCheckpoints", out var cp) && (int)cp >= 0 ? (int)cp : (int?)null;
        var lapRace = mapInfo.TryGetValue("LapRace", out var lr) && (bool)lr;
        var nbLaps = mapInfo.TryGetValue("NbLaps", out var nl) ? (int)nl : 0;
        return new ChallengeSummary((string)mapInfo["Name"], nbCheckpoints, lapRace, nbLaps);
    }

    public void On(string methodName, Func<object[], CancellationToken, Task> handler)
    {
        Raw.On(methodName, handler);
    }

    public async ValueTask DisposeAsync()
    {
        if (raw is not null)
        {
            await raw.DisposeAsync();
            raw = null;
        }
    }

    public void Dispose()
    {
        raw?.Dispose();
        raw = null;
    }
}
