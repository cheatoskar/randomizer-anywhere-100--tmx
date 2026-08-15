using System.Text.Json;

namespace RandomizerAnywhere;

internal sealed class ImpossibleMaps
{
    private const string SheetCsvUrl = "https://docs.google.com/spreadsheets/d/1fqmzFGPIFBlJuxlwnPJSh1nCTTxqWXtHtvP5OUxE4Ow/export?format=csv&gid=605781157";
    private const int SheetTrackIdColumn = 1;
    private const int SheetFirstDataRow = 4;

    private readonly HttpClient http;
    private readonly string filePath = Path.Combine(AppContext.BaseDirectory, "impossible-maps.json");

    private readonly HashSet<int> excludedTrackIds = [];
    private readonly object gate = new();

    public ImpossibleMaps(HttpClient http)
    {
        this.http = http;
    }

    public bool Contains(int trackId)
    {
        lock (gate)
        {
            return excludedTrackIds.Contains(trackId);
        }
    }

    public int[] AllIds()
    {
        lock (gate)
        {
            return [.. excludedTrackIds];
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                var ids = JsonSerializer.Deserialize<int[]>(json) ?? [];

                lock (gate)
                {
                    foreach (var id in ids)
                    {
                        excludedTrackIds.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to read impossible-maps.json - {ex.Message}");
            }
        }

        try
        {
            var csv = await FetchFollowingRedirectsAsync(SheetCsvUrl, cancellationToken);
            var addedFromSheet = 0;

            using var reader = new StringReader(csv);
            for (var rowIndex = 1; reader.ReadLine() is { } line; rowIndex++)
            {
                if (rowIndex < SheetFirstDataRow)
                {
                    continue;
                }

                var fields = ParseCsvLine(line);

                if (fields.Length <= SheetTrackIdColumn || !int.TryParse(fields[SheetTrackIdColumn], out var trackId))
                {
                    continue;
                }

                lock (gate)
                {
                    if (excludedTrackIds.Add(trackId))
                    {
                        addedFromSheet++;
                    }
                }
            }

            Console.WriteLine($"Impossible maps: loaded {addedFromSheet} new track id(s) from the sheet.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: failed to fetch impossible-maps sheet, continuing with local list only - {ex.Message}");
        }

        await SaveAsync(cancellationToken);
    }

    private async Task<string> FetchFollowingRedirectsAsync(string url, CancellationToken cancellationToken, int maxRedirects = 5)
    {
        for (var i = 0; i < maxRedirects; i++)
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode is System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found
                or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.PermanentRedirect)
            {
                url = response.Headers.Location?.OriginalString ?? throw new Exception("Redirect response had no Location header.");
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        throw new Exception($"Too many redirects fetching {url}.");
    }

    public async Task AddAsync(int trackId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            excludedTrackIds.Add(trackId);
        }

        await SaveAsync(cancellationToken);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        int[] ids;
        lock (gate)
        {
            ids = [.. excludedTrackIds];
        }

        var json = JsonSerializer.Serialize(ids);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return [.. fields];
    }
}
