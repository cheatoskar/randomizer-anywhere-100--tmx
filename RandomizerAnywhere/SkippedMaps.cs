using System.Text.Json;

namespace RandomizerAnywhere;

// This server's own "never play these" list, for maps that can't be played here - a map built with
// blocks the game doesn't have, say, which fails to load for every player. Deliberately separate
// from ImpossibleMaps: that one is the shared, community-reviewed list pulled from the sheet and
// reported to Discord. This one is local to the server, is never sent anywhere, and is not
// overwritten by the sheet refresh. Persisted to skipped-maps.json so it survives restarts, and
// plain JSON so it can be edited by hand (a flat array of TMX track ids).
internal sealed class SkippedMaps
{
    private readonly string filePath = Path.Combine(AppContext.BaseDirectory, "skipped-maps.json");
    private readonly HashSet<int> trackIds = [];
    private readonly object gate = new();

    public bool Contains(int trackId)
    {
        lock (gate)
        {
            return trackIds.Contains(trackId);
        }
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return trackIds.Count;
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            var ids = JsonSerializer.Deserialize<int[]>(json) ?? [];

            lock (gate)
            {
                foreach (var id in ids)
                {
                    trackIds.Add(id);
                }
            }

            Console.WriteLine($"Skipped maps: {ids.Length} map(s) on this server's skip list.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: failed to read skipped-maps.json - {ex.Message}");
        }
    }

    // Returns false if the map was already on the list.
    public async Task<bool> AddAsync(int trackId, CancellationToken cancellationToken = default)
    {
        int[] snapshot;

        lock (gate)
        {
            if (!trackIds.Add(trackId))
            {
                return false;
            }

            snapshot = [.. trackIds.Order()];
        }

        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(snapshot), cancellationToken);
        return true;
    }
}
