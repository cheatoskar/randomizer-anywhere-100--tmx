using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace RandomizerAnywhere;

internal sealed class ReplayServer
{
    private readonly string replaysDir;
    private readonly string statusDir;
    private readonly ushort port;

    private WebApplication? app;

    public ReplayServer(string replaysDir, string statusDir, ushort port)
    {
        this.replaysDir = replaysDir;
        this.statusDir = statusDir;
        this.port = port;
    }

    public async Task StartAsync()
    {
        Directory.CreateDirectory(replaysDir);
        Directory.CreateDirectory(statusDir);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        app = builder.Build();

        app.MapGet("/", () => Results.Content(IndexHtml, "text/html"));

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(replaysDir),
            RequestPath = "/replays",
            ServeUnknownFileTypes = true,
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(statusDir),
            RequestPath = "",
        });

        await app.StartAsync();
    }

    private const string IndexHtml = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>100% TMX Project</title>
        <style>
            :root { color-scheme: dark; }
            body {
                margin: 0; padding: 2rem 1rem; min-height: 100vh; box-sizing: border-box;
                background: #14161c; color: #e6e6e6;
                font-family: -apple-system, Segoe UI, Roboto, sans-serif;
                display: flex; justify-content: center;
            }
            main { width: 100%; max-width: 640px; }
            h1 { font-size: 1.4rem; margin: 0 0 0.25rem; }
            .sub { color: #9aa0ab; margin-bottom: 1.5rem; font-size: 0.9rem; }
            .card {
                background: #1c1f27; border: 1px solid #2a2e38; border-radius: 12px;
                padding: 1.25rem; margin-bottom: 1rem;
            }
            .card h2 { font-size: 0.8rem; text-transform: uppercase; letter-spacing: 0.05em;
                color: #9aa0ab; margin: 0 0 0.75rem; }
            .map-name { font-size: 1.15rem; font-weight: 600; }
            .map-link { color: #5aa9ff; text-decoration: none; font-size: 0.9rem; }
            .map-link:hover { text-decoration: underline; }
            .stat-row { display: flex; gap: 1.5rem; margin-top: 0.75rem; }
            .stat { }
            .stat .value { font-size: 1.3rem; font-weight: 700; }
            .stat .label { font-size: 0.75rem; color: #9aa0ab; }
            ol { margin: 0; padding-left: 1.4rem; }
            li { padding: 0.25rem 0; }
            .empty { color: #9aa0ab; font-style: italic; }
            footer { text-align: center; color: #565c68; font-size: 0.75rem; margin-top: 1.5rem; }
        </style>
        </head>
        <body>
        <main>
            <h1 id="server-name">100% TMX Project</h1>
            <div class="sub" id="session-state">loading...</div>

            <div class="card">
                <h2>Current Map</h2>
                <div class="map-name" id="map-name">-</div>
                <a class="map-link" id="map-link" href="#" target="_blank" rel="noopener"></a>
                <div class="stat-row">
                    <div class="stat">
                        <div class="value" id="player-count">-</div>
                        <div class="label">players online</div>
                    </div>
                </div>
            </div>

            <div class="card">
                <h2>Top Finishers</h2>
                <ol id="top-list"><li class="empty">loading...</li></ol>
            </div>

            <footer id="updated-at"></footer>
        </main>
        <script>
            async function refresh() {
                try {
                    const res = await fetch('/status.json', { cache: 'no-store' });
                    if (!res.ok) return;
                    const s = await res.json();

                    document.getElementById('server-name').textContent = s.ServerName || '100% TMX Project';
                    document.getElementById('session-state').textContent = s.SessionActive ? 'Live' : 'Session stopped';
                    document.getElementById('player-count').textContent = s.PlayerCount ?? '0';
                    document.getElementById('map-name').textContent = s.CurrentMapName || 'No map loaded';

                    const link = document.getElementById('map-link');
                    if (s.CurrentMapUrl) {
                        link.href = s.CurrentMapUrl;
                        link.textContent = s.CurrentMapUrl;
                        link.style.display = 'inline';
                    } else {
                        link.style.display = 'none';
                    }

                    const list = document.getElementById('top-list');
                    if (s.Top && s.Top.length > 0) {
                        list.innerHTML = s.Top.map(e => `<li>${escapeHtml(e.LastNickname)} - ${e.Finishes} finish(es)</li>`).join('');
                    } else {
                        list.innerHTML = '<li class="empty">No finishes recorded yet.</li>';
                    }

                    if (s.UpdatedAt) {
                        document.getElementById('updated-at').textContent = 'Updated ' + new Date(s.UpdatedAt).toLocaleTimeString();
                    }
                } catch {
                    // server not reachable right now, keep last known state on screen
                }
            }

            function escapeHtml(text) {
                const div = document.createElement('div');
                div.textContent = text;
                return div.innerHTML;
            }

            refresh();
            setInterval(refresh, 5000);
        </script>
        </body>
        </html>
        """;
}
