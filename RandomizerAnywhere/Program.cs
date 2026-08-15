using Jab;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using RandomizerAnywhere;
using RandomizerAnywhere.Config;
using System.Net.Sockets;
using System.Runtime.InteropServices;

using var cts = new CancellationTokenSource();

var provider = new AppServiceProvider();

var impossibleMaps = provider.GetRequiredService<ImpossibleMaps>();
await impossibleMaps.LoadAsync();

var leaderboard = provider.GetRequiredService<Leaderboard>();
await leaderboard.LoadAsync();

var serverSetup = provider.GetRequiredService<ServerSetup>();
await serverSetup.TrySetupAsync();

using var sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    cts.Cancel();
    serverSetup.StopServer();
});

var appConfig = provider.GetRequiredService<AppConfig>();
if (appConfig.DedicatedServerMode)
{
    var replayServer = new ReplayServer(serverSetup.ReplaysDir, appConfig.ReplayServerPort);
    await replayServer.StartAsync();
}

try
{
    var randomizerSetup = provider.GetRequiredService<RandomizerSetup>();
    await randomizerSetup.RunAsync(cts.Token);

    var randomizerGame = provider.GetRequiredService<RandomizerGame>();
    await randomizerGame.RunAsync(cts.Token);
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    // graceful shutdown requested (e.g. systemd stop)
}
catch (Exception ex) when (cts.IsCancellationRequested && ex is IOException or SocketException)
{
    // the XML-RPC socket can drop out from under us while the dedicated server process is
    // being killed during shutdown; expected in that case, not a real failure
}
finally
{
    serverSetup.StopServer();
}

[ServiceProvider]
[Singleton(typeof(HttpClient), Factory = nameof(CreateHttpClient))]
[Singleton(typeof(AppConfig), Factory = nameof(CreateAppConfig))]
[Transient(typeof(TmxRules))]
[Transient(typeof(ServerSetup))]
[Transient(typeof(RandomizerSetup))]
[Singleton(typeof(RemoteClient))]
[Transient(typeof(RandomizerGame))]
[Singleton(typeof(ImpossibleMaps))]
[Singleton(typeof(DiscordNotifier))]
[Singleton(typeof(Leaderboard))]
internal partial class AppServiceProvider
{
    public static HttpClient CreateHttpClient()
    {
        var httpResilienceOptions = new HttpStandardResilienceOptions();
        httpResilienceOptions.Retry.MaxRetryAttempts = int.MaxValue;

        var httpRetryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(httpResilienceOptions.Retry)
            .Build();

        return new HttpClient(new ResilienceHandler(httpRetryPipeline)
        {
            InnerHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                AllowAutoRedirect = false
            }
        });
    }

    public static AppConfig CreateAppConfig()
    {
        var defaultConfigPath = Path.Combine(AppContext.BaseDirectory, "config.default.toml");
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.toml");

        if (!File.Exists(configPath))
        {
            File.Copy(defaultConfigPath, configPath);
        }

        var globalConfig = TomlLoader.LoadGlobalConfig(configPath);
        var cmdConfig = CmdConfig.Parse(Environment.GetCommandLineArgs());
        var appConfig = new AppConfig
        {
            Game = Configurator.GetOrAskEnum(globalConfig.Game, cmdConfig.Game, "RANDANY_GAME", "a game"),
            TmxGame = Configurator.GetOptionalEnum(globalConfig.TmxGame, cmdConfig.TmxGame, "RANDANY_TMX_GAME"),
            BindIP = Configurator.GetIP(globalConfig.BindIP, cmdConfig.BindIP, "RANDANY_BIND_IP"),
            XmlRpcPort = Configurator.GetNumber(globalConfig.XmlRpcPort, cmdConfig.XmlRpcPort, "RANDANY_XMLRPC_PORT"),
            AutoSkipMode = Configurator.GetEnum<AutoSkipMode>(globalConfig.AutoSkipMode, cmdValue: null, "RANDANY_AUTO_SKIP_MODE"),
            DownloadUrls = globalConfig.DownloadUrls
                .ToDictionary(
                    x => Enum.Parse<DedicatedServerType>(x.Key, ignoreCase: true),
                    x => x.Value),
            TmxQuery = globalConfig.TmxQuery,
            TmxQueryOverride = cmdConfig.TmxQuery,
            DedicatedServerMode = Configurator.GetBool(cfgValue: null, cmdConfig.DedicatedServerMode, "RANDANY_DEDICATED"),
            SkipSetup = Configurator.GetBool(cfgValue: null, cmdConfig.SkipSetup, "RANDANY_SKIP_SETUP"),
            TimeLimit = new(globalConfig.TimeLimit),
            CallVoteOnFinish = Configurator.GetBool(globalConfig.CallVoteOnFinish, cmdValue: null, "RANDANY_CALLVOTE_ON_FINISH"),
            WelcomeMessage = globalConfig.WelcomeMessage.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            ServerName = Configurator.GetString(globalConfig.ServerName, cmdConfig.ServerName, "RANDANY_SERVER_NAME"),
            GameSettings = Configurator.GetString(globalConfig.GameSettings, cmdValue: null, "RANDANY_GAMESETTINGS"),
            AutoStart = Configurator.GetBool(globalConfig.AutoStart, cmdValue: null, "RANDANY_AUTO_START"),
            AdminLogins = Configurator.GetString(globalConfig.AdminLogins, cmdValue: null, "RANDANY_ADMIN_LOGINS")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            PublicHost = Configurator.GetString(globalConfig.PublicHost, cmdValue: null, "RANDANY_PUBLIC_HOST"),
            ReplayServerPort = Configurator.GetNumber(globalConfig.ReplayServerPort, cmdValue: null, "RANDANY_REPLAY_SERVER_PORT"),
            Lan = Configurator.GetBool(globalConfig.Lan, cmdValue: null, "RANDANY_LAN"),
            DiscordWebhookUrl = Configurator.GetString(globalConfig.DiscordWebhookUrl, cmdValue: null, "RANDANY_DISCORD_WEBHOOK_URL"),
            DiscordWebhookUrlHard = Configurator.GetString(globalConfig.DiscordWebhookUrlHard, cmdValue: null, "RANDANY_DISCORD_WEBHOOK_URL_HARD"),
        };

        if (!string.IsNullOrWhiteSpace(globalConfig.Preset))
        {
            var presetPath = Path.Combine(AppContext.BaseDirectory, "Presets", globalConfig.Preset + ".toml");

            if (File.Exists(presetPath))
            {
                var presetConfig = TomlLoader.LoadPresetConfig(presetPath);
                presetConfig?.Apply(appConfig);
                appConfig.LastPreset = presetConfig;
            }
            else
            {
                Console.WriteLine($"Preset '{globalConfig.Preset}' not found.");
            }
        }

        return appConfig;
    }
}