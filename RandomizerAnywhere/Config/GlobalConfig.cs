namespace RandomizerAnywhere.Config;

internal sealed class GlobalConfig
{
    public string Game { get; set; } = string.Empty;
    public string TmxGame { get; set; } = string.Empty;
    public string BindIP { get; set; } = string.Empty;
    public ushort XmlRpcPort { get; set; }
    public string AutoSkipMode { get; set; } = string.Empty;
    public int TimeLimit { get; set; }
    public bool CallVoteOnFinish { get; set; }
    public string GameSettings { get; set; } = string.Empty;

    public string Preset { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;
    public string ServerComment { get; set; } = string.Empty;
    public string WelcomeMessage { get; set; } = string.Empty;

    public bool AutoStart { get; set; }
    public string AdminLogins { get; set; } = string.Empty;

    public string PublicHost { get; set; } = string.Empty;
    public ushort ReplayServerPort { get; set; }
    public bool Lan { get; set; }
    public string DiscordWebhookUrl { get; set; } = string.Empty;
    public string DiscordWebhookUrlHard { get; set; } = string.Empty;

    public Dictionary<string, string> DownloadUrls { get; set; } = [];
    public Dictionary<string, object> TmxQuery { get; set; } = [];
}
