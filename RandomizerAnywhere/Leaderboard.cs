using System.Text.Json;

namespace RandomizerAnywhere;

internal sealed record LeaderboardEntry(string LastNickname, int Finishes);

internal sealed class Leaderboard
{
    private readonly string filePath = Path.Combine(AppContext.BaseDirectory, "leaderboard.json");
    private readonly Dictionary<string, LeaderboardEntry> entries = [];
    private readonly object gate = new();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, LeaderboardEntry>>(json) ?? [];

            lock (gate)
            {
                foreach (var (login, entry) in loaded)
                {
                    entries[login] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: failed to read leaderboard.json - {ex.Message}");
        }
    }

    public async Task RecordFinishAsync(string login, string nickname, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var finishes = entries.TryGetValue(login, out var existing) ? existing.Finishes + 1 : 1;
            entries[login] = new LeaderboardEntry(nickname, finishes);
        }

        await SaveAsync(cancellationToken);
    }

    public IReadOnlyList<LeaderboardEntry> GetTop(int count)
    {
        lock (gate)
        {
            return entries.Values
                .OrderByDescending(e => e.Finishes)
                .Take(count)
                .ToList();
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, LeaderboardEntry> snapshot;
        lock (gate)
        {
            snapshot = new Dictionary<string, LeaderboardEntry>(entries);
        }

        var json = JsonSerializer.Serialize(snapshot);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }
}
