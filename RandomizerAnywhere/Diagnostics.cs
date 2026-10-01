using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace RandomizerAnywhere;

// A "flight recorder" for the stalls and restarts that only show up on the live server.
//
// Everything interesting (every RPC with its duration, every callback arriving, each step of a map
// change) is written into a small in-memory ring buffer at almost no cost and prints nothing. When
// something goes wrong - the connection closes, the callback stream is declared dead, a handler
// runs long, the status write times out - Dump prints the buffered history plus a snapshot of the
// process, the dedicated server and the machine. The journal then holds what happened in the
// seconds BEFORE the event, not just the event itself. Every line starts with "[diag]" so it can
// be pulled out with: journalctl -u 100tmx | grep "\[diag\]"
internal static class Diagnostics
{
    private const int RingCapacity = 400;

    private static readonly ConcurrentQueue<string> ring = new();
    private static readonly ConcurrentDictionary<long, (string Name, long StartedAt)> inFlightHandlers = new();
    private static long handlerSequence;

    private static readonly object dumpGate = new();
    private static DateTimeOffset lastDumpAt = DateTimeOffset.MinValue;

    private static TimeSpan lastServerCpu;
    private static DateTimeOffset lastServerCpuAt = DateTimeOffset.MinValue;

    public static void Trace(string message)
    {
        ring.Enqueue($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {message}");

        while (ring.Count > RingCapacity && ring.TryDequeue(out _))
        {
        }
    }

    public static long HandlerStarted(string name)
    {
        var id = Interlocked.Increment(ref handlerSequence);
        inFlightHandlers[id] = (name, Stopwatch.GetTimestamp());
        return id;
    }

    public static void HandlerFinished(long id) => inFlightHandlers.TryRemove(id, out _);

    // Prints the recent history and a snapshot. Rate limited so a storm of related events (a stall
    // trips several detectors at once) produces one report, not five.
    public static void Dump(string reason, string? extra = null, int recentLines = 60, bool force = false)
    {
        lock (dumpGate)
        {
            var now = DateTimeOffset.UtcNow;

            if (!force && now - lastDumpAt < TimeSpan.FromSeconds(20))
            {
                Console.WriteLine($"[diag] (another report was printed <20s ago; trigger: {reason})");
                return;
            }

            lastDumpAt = now;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[diag] ===== {reason} =====");

        if (extra is not null)
        {
            sb.AppendLine($"[diag] {extra}");
        }

        sb.AppendLine($"[diag] {Snapshot()}");

        var recent = ring.ToArray();
        var skip = Math.Max(0, recent.Length - recentLines);
        sb.AppendLine($"[diag] last {recent.Length - skip} recorded events (oldest first):");

        foreach (var line in recent.Skip(skip))
        {
            sb.AppendLine($"[diag]   {line}");
        }

        sb.Append("[diag] ===== end =====");
        Console.WriteLine(sb.ToString());
    }

    // One line describing the controller process, the dedicated server process and the box.
    public static string Snapshot()
    {
        var sb = new StringBuilder();

        try
        {
            using var self = Process.GetCurrentProcess();
            ThreadPool.GetAvailableThreads(out var freeWorkers, out _);
            ThreadPool.GetMaxThreads(out var maxWorkers, out _);

            sb.Append($"controller: mem={self.WorkingSet64 / 1048576}MB threads={self.Threads.Count} ")
              .Append($"pool(busy={maxWorkers - freeWorkers} queued={ThreadPool.PendingWorkItemCount}) ")
              .Append($"gc(gen2={GC.CollectionCount(2)} pause={GC.GetTotalPauseDuration().TotalMilliseconds:0}ms) ");
        }
        catch (Exception ex)
        {
            sb.Append($"controller: n/a ({ex.GetType().Name}) ");
        }

        try
        {
            var servers = Process.GetProcessesByName("TrackmaniaServer");

            if (servers.Length == 0)
            {
                sb.Append("dedicated: NOT RUNNING ");
            }
            else
            {
                foreach (var server in servers)
                {
                    var cpu = server.TotalProcessorTime;
                    var now = DateTimeOffset.UtcNow;
                    var cpuPercent = lastServerCpuAt == DateTimeOffset.MinValue || now <= lastServerCpuAt
                        ? double.NaN
                        : (cpu - lastServerCpu).TotalSeconds / (now - lastServerCpuAt).TotalSeconds * 100;

                    lastServerCpu = cpu;
                    lastServerCpuAt = now;

                    sb.Append($"dedicated: pid={server.Id} up={(DateTime.Now - server.StartTime):d\\.hh\\:mm\\:ss} ")
                      .Append($"mem={server.WorkingSet64 / 1048576}MB threads={server.Threads.Count} ")
                      .Append($"cpu={(double.IsNaN(cpuPercent) ? "?" : cpuPercent.ToString("0"))}% ");
                    server.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            sb.Append($"dedicated: n/a ({ex.GetType().Name}) ");
        }

        try
        {
            if (File.Exists("/proc/loadavg"))
            {
                sb.Append($"load={File.ReadAllText("/proc/loadavg").Split(' ').Take(3).Aggregate((a, b) => a + " " + b)} ");
            }
        }
        catch (Exception)
        {
            // not on Linux / not readable - the rest of the snapshot is still useful
        }

        var running = inFlightHandlers.Values.ToArray();

        if (running.Length > 0)
        {
            sb.Append("handlers-running: ")
              .Append(string.Join(", ", running.Select(h => $"{h.Name}({Stopwatch.GetElapsedTime(h.StartedAt).TotalSeconds:0.0}s)")));
        }
        else
        {
            sb.Append("handlers-running: none");
        }

        return sb.ToString();
    }
}
