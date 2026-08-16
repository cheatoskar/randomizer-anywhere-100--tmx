using RandomizerAnywhere.Config;
using System.Buffers;
using System.Globalization;
using System.Linq;
using System.Net.Http.Json;
using System.Text;

namespace RandomizerAnywhere;

internal sealed class TmxRules
{
    private const int MaxRecencyCheckedAttempts = 20;
    private const int MaxTotalAttempts = 100;
    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(1);

    private readonly HttpClient http;
    private readonly AppConfig config;
    private readonly ImpossibleMaps impossibleMaps;

    private readonly GameTitle game;
    private readonly Dictionary<int, DateTimeOffset> recentlyServed = [];

    // maps reported via /imp - excluded for the rest of this process's uptime, not written to
    // disk/the shared sheet. That's the human-reviewed permanent list (impossibleMaps above);
    // this is just "don't show it again until someone actually reviews the report"
    private readonly HashSet<int> sessionExcludedTrackIds = [];

    public void ExcludeForSession(int trackId) => sessionExcludedTrackIds.Add(trackId);

    public TmxRules(HttpClient http, AppConfig config, ImpossibleMaps impossibleMaps)
    {
        this.http = http;
        this.config = config;
        this.impossibleMaps = impossibleMaps;

        game = config.TmxGame ?? config.Game;
    }

    public string BuildQuery()
    {
        if (config.SourceFromImpossibleList)
        {
            var ids = impossibleMaps.AllIds();
            return ids.Length == 0
                ? "id=0" // no impossible maps tracked yet - deliberately yields "not found" rather than falling back to the full pool
                : $"id={Uri.EscapeDataString(string.Join(',', ids))}";
        }

        var b = new StringBuilder();

        var first = true;

        foreach (var (key, value) in config.TmxQuery)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!first)
            {
                b.Append('&');
            }

            first = false;

            b.Append(Uri.EscapeDataString(key));
            b.Append('=');
            b.Append(Uri.EscapeDataString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
        }

        return b.ToString();
    }

    public string GetRandomTrackUrl() => $"https://{GetSiteUrl()}/trackrandom";
    public string GetTrackGbxUrl(string trackId) => $"https://{GetSiteUrl()}/trackgbx/{trackId}";

    private static readonly string[] DifficultyLabels = ["Beginner", "Intermediate", "Expert", "Lunatic"];

    // 0=Beginner, 1=Intermediate, 2=Expert, 3=Lunatic - verified live against the real API
    // (tmnf.exchange/trackshow/<id> renders these exact labels for these exact values)
    public async Task<(string DifficultyLabel, int Awards)> GetTrackMetaAsync(int trackId, CancellationToken cancellationToken = default)
    {
        var url = $"https://{GetSiteUrl()}/api/tracks?id={trackId}&fields=Difficulty,Awards";
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TmxApiTracksResponse>(cancellationToken);
        var result = payload?.Results?.FirstOrDefault();

        if (result is null)
        {
            throw new InvalidOperationException($"TMX has no metadata for track {trackId}.");
        }

        var difficultyLabel = result.Difficulty >= 0 && result.Difficulty < DifficultyLabels.Length
            ? DifficultyLabels[result.Difficulty]
            : $"Difficulty {result.Difficulty}";

        return (difficultyLabel, result.Awards);
    }

    public async Task<string> GetTrackNameAsync(int trackId, CancellationToken cancellationToken = default)
    {
        var url = $"https://{GetSiteUrl()}/api/tracks?id={trackId}&fields=TrackName";
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TmxApiTracksResponse>(cancellationToken);
        var name = payload?.Results?.FirstOrDefault()?.TrackName;

        return string.IsNullOrWhiteSpace(name) ? $"Track {trackId}" : name;
    }

    private sealed record TmxApiTracksResponse(List<TmxApiTrackResult>? Results);
    private sealed record TmxApiTrackResult(int Difficulty, int Awards, string? TrackName = null);

    // tag id -> display name, verified live against tmnf.exchange/api/meta/tags (2026-08-16) -
    // this is TMX's community "style" classification (LOL, Tech, RPG, ...), separate from the
    // "Routes"/multilap axis used by the "routes" TmxQuery filter
    private static readonly Dictionary<int, string> TagNames = new()
    {
        [0] = "Race", [1] = "Stunt", [2] = "Maze", [3] = "Offroad", [4] = "Multilap",
        [5] = "FullSpeed", [6] = "LOL", [7] = "Tech", [8] = "SpeedTech", [9] = "RPG",
        [10] = "PressForward", [11] = "Trial", [12] = "Grass", [13] = "Story",
        [14] = "Nascar", [15] = "Speedfun", [16] = "Endurance", [17] = "Altered Nadeo",
        [18] = "Transitional",
    };

    public sealed record TrackDetails(string Name, string Author, string DifficultyLabel, int Awards, int AuthorTimeMs, IReadOnlyList<string> Tags);

    // richer metadata for a single specific track (name, author, difficulty, AT, style tags) -
    // used to show what a player is voting to load, before it's ever downloaded/enqueued.
    // "Uploader.Name" is TMX's nested-field syntax for the author's display name - "Username"/
    // "Author"/etc all 400 with "field does not exist", verified live against the real API
    public async Task<TrackDetails> GetTrackDetailsAsync(int trackId, CancellationToken cancellationToken = default)
    {
        var url = $"https://{GetSiteUrl()}/api/tracks?id={trackId}&fields=TrackName,Uploader.Name,Difficulty,Awards,AuthorTime,Tags";
        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<TmxApiTrackDetailsResponse>(cancellationToken);
        var result = payload?.Results?.FirstOrDefault();

        if (result is null)
        {
            throw new InvalidOperationException($"TMX has no metadata for track {trackId}.");
        }

        var difficultyLabel = result.Difficulty >= 0 && result.Difficulty < DifficultyLabels.Length
            ? DifficultyLabels[result.Difficulty]
            : $"Difficulty {result.Difficulty}";

        var tagNames = (result.Tags ?? []).Select(id => TagNames.GetValueOrDefault(id, $"Tag {id}")).ToList();
        var authorName = result.Uploader?.Name is { Length: > 0 } uploaderName ? uploaderName : "unknown";
        var trackName = result.TrackName is { Length: > 0 } name ? name : $"Track {trackId}";

        return new TrackDetails(trackName, authorName, difficultyLabel, result.Awards, result.AuthorTime, tagNames);
    }

    private sealed record TmxApiTrackDetailsResponse(List<TmxApiTrackDetailsResult>? Results);
    private sealed record TmxApiTrackDetailsResult(string? TrackName, TmxApiUploader? Uploader, int Difficulty, int Awards, int AuthorTime, List<int>? Tags);
    private sealed record TmxApiUploader(string? Name);

    // downloads a SPECIFIC track by id directly, bypassing /trackrandom entirely - unlike
    // NextMapGbxAsync this does NOT check impossibleMaps/sessionExcludedTrackIds, since loading a
    // known id is an explicit, deliberate request (e.g. "let's all try this hard map together"),
    // not a random-pool pick that those exclusions are meant to filter
    public async Task<InMemoryFile> GetMapGbxByIdAsync(int trackId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(GetTrackGbxUrl(trackId.ToString(CultureInfo.InvariantCulture)), cancellationToken);
        response.EnsureSuccessStatusCode();

        var gbxBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName ?? (response.RequestMessage?.RequestUri?.Segments.Last() + ".Gbx");

        recentlyServed[trackId] = DateTimeOffset.UtcNow;
        return new InMemoryFile(GetValidFileName(fileName), gbxBytes, trackId);
    }

    public string GetSiteUrl() => game switch
    {
        GameTitle.TMNF => "tmnf.exchange",
        GameTitle.TMUF => "tmuf.exchange",
        GameTitle.TMN => "nations.tm-exchange.com",
        GameTitle.TMS => "sunrise.tm-exchange.com",
        GameTitle.TMO => "original.tm-exchange.com",
        _ => throw new Exception("Unknown game title"),
    };

    public static SearchValues<char> InvalidFileNameCharSearchValues { get; } = SearchValues.Create([
        '\"', '<', '>', '|', '\0',
        (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
        (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
        (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
        (char)31, ':', '*', '?', '\\', '/'
    ]);

    public async Task<InMemoryFile> NextMapGbxAsync(CancellationToken cancellationToken = default)
    {
        var (trackResponse, trackId) = await NextMapGbxResponseAsync(cancellationToken);
        using (trackResponse)
        {
            var gbxBytes = await trackResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            var fileName = trackResponse.Content.Headers.ContentDisposition?.FileNameStar
                ?? trackResponse.Content.Headers.ContentDisposition?.FileName ?? (trackResponse.RequestMessage?.RequestUri?.Segments.Last() + ".Gbx");

            return new InMemoryFile(GetValidFileName(fileName), gbxBytes, trackId);
        }
    }

    private async Task<(HttpResponseMessage Response, int TrackId)> NextMapGbxResponseAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxTotalAttempts; attempt++)
        {
            var tmxRandomUrl = $"{GetRandomTrackUrl()}?{config.TmxQueryOverride ?? BuildQuery()}";

            using var request = new HttpRequestMessage(HttpMethod.Head, tmxRandomUrl);
            using var response = await http.SendAsync(request, cancellationToken);

            var trackRelativePath = response.Headers.Location?.OriginalString ?? throw new Exception("Failed to get track relative path.");
            var trackIdString = trackRelativePath.Substring(trackRelativePath.LastIndexOf('/') + 1);

            if (!int.TryParse(trackIdString, out var trackId))
            {
                continue;
            }

            if (!config.SourceFromImpossibleList && (impossibleMaps.Contains(trackId) || sessionExcludedTrackIds.Contains(trackId)))
            {
                continue;
            }

            PruneRecentlyServed();

            if (recentlyServed.ContainsKey(trackId) && attempt <= MaxRecencyCheckedAttempts)
            {
                continue;
            }

            Console.WriteLine("Next track ID: " + trackId);
            recentlyServed[trackId] = DateTimeOffset.UtcNow;

            return (await http.GetAsync(GetTrackGbxUrl(trackIdString), cancellationToken), trackId);
        }

        throw new InvalidOperationException($"Could not find a servable map after {MaxTotalAttempts} attempts (all candidates were excluded).");
    }

    private void PruneRecentlyServed()
    {
        var cutoff = DateTimeOffset.UtcNow - RecentWindow;
        var expired = recentlyServed.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();

        foreach (var id in expired)
        {
            recentlyServed.Remove(id);
        }
    }

    private static string GetValidFileName(string fileName)
    {
        var buffer = ArrayPool<char>.Shared.Rent(fileName.Length);
        var bufferIndex = 0;

        foreach (var c in fileName)
        {
            // non-ASCII stays out too, not just the Windows-invalid characters: HttpClient parses
            // the Content-Disposition "filename" header as Latin-1 per the historic HTTP header
            // spec, so a map name with stylized Unicode (common on TMX) comes through as mojibake.
            // That mojibake string then gets written to disk AND re-encoded as UTF-8 into the match
            // settings XML separately, and the two don't round-trip to the same bytes - the
            // dedicated server ends up looking for a filename slightly different from the one
            // actually on disk and fails to boot with "Track unknown"
            buffer[bufferIndex++] = InvalidFileNameCharSearchValues.Contains(c) || c > 127 ? '_' : c;
        }

        var result = new string(buffer, 0, bufferIndex);
        ArrayPool<char>.Shared.Return(buffer);

        return result;
    }
}
