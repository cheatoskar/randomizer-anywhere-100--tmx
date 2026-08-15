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
            :root {
                color-scheme: dark;
                --bg: #0f1115; --bg-alt: #14161c;
                --card: #1a1d25; --card-border: #262a35;
                --text: #eef0f4; --text-dim: #8b91a0; --text-faint: #565c68;
                --accent: #ff5f5f; --accent-2: #ffb648; --accent-3: #4fd1c5;
                --gold: #ffd75e; --silver: #c9d2e0; --bronze: #d99a5b;
            }
            * { box-sizing: border-box; }
            body {
                margin: 0; padding: 1.75rem 1rem 3rem; min-height: 100vh;
                background:
                    radial-gradient(1200px 500px at 50% -10%, rgba(255, 95, 95, 0.12), transparent),
                    linear-gradient(180deg, var(--bg-alt), var(--bg) 320px);
                color: var(--text);
                font-family: -apple-system, "Segoe UI", Roboto, sans-serif;
                display: flex; justify-content: center;
            }
            main { width: 100%; max-width: 760px; }

            header { display: flex; align-items: center; justify-content: space-between;
                flex-wrap: wrap; gap: 0.5rem; margin-bottom: 1.5rem; }
            h1 { font-size: 1.5rem; margin: 0; letter-spacing: -0.01em;
                background: linear-gradient(90deg, var(--accent), var(--accent-2));
                -webkit-background-clip: text; background-clip: text; color: transparent; }
            .badges { display: flex; gap: 0.5rem; flex-wrap: wrap; }
            .badge { display: inline-flex; align-items: center; gap: 0.35rem;
                font-size: 0.75rem; font-weight: 600; padding: 0.3rem 0.65rem;
                border-radius: 999px; border: 1px solid var(--card-border); color: var(--text-dim); }
            .badge.live { color: #7cf29c; border-color: rgba(124, 242, 156, 0.35);
                background: rgba(124, 242, 156, 0.08); }
            .badge.live .dot { width: 6px; height: 6px; border-radius: 50%; background: #7cf29c;
                box-shadow: 0 0 8px #7cf29c; animation: pulse 1.6s infinite; }
            .badge.stopped { color: #ff8f8f; border-color: rgba(255, 143, 143, 0.35);
                background: rgba(255, 143, 143, 0.08); }
            @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.35; } }

            .card {
                background: var(--card); border: 1px solid var(--card-border); border-radius: 14px;
                padding: 1.25rem; margin-bottom: 1rem;
            }
            .card h2 { font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.08em;
                color: var(--text-dim); margin: 0 0 0.85rem; font-weight: 700; }

            .map-card { display: flex; gap: 1.1rem; align-items: stretch; }
            .map-image-wrap { flex: 0 0 auto; width: 200px; aspect-ratio: 16 / 12; border-radius: 10px;
                overflow: hidden; background: #0c0e12; border: 1px solid var(--card-border); }
            .map-image-wrap img { width: 100%; height: 100%; object-fit: cover; display: block; }
            .map-info { flex: 1; min-width: 0; display: flex; flex-direction: column; justify-content: center; }
            .map-name { font-size: 1.2rem; font-weight: 700; overflow-wrap: anywhere; }
            .map-link { color: var(--accent-3); text-decoration: none; font-size: 0.85rem;
                display: inline-block; margin-top: 0.3rem; }
            .map-link:hover { text-decoration: underline; }
            .stat-row { display: flex; gap: 1.5rem; margin-top: 0.9rem; }
            .stat .value { font-size: 1.35rem; font-weight: 800; }
            .stat .label { font-size: 0.7rem; color: var(--text-dim); text-transform: uppercase;
                letter-spacing: 0.04em; }

            .racer { display: flex; align-items: center; gap: 0.75rem; padding: 0.5rem 0; }
            .racer + .racer { border-top: 1px solid var(--card-border); }
            .racer .rank { width: 1.4rem; text-align: center; font-weight: 700; color: var(--text-faint); }
            .racer.leader .rank { color: var(--gold); }
            .racer .name { flex: 0 0 auto; width: 9rem; overflow: hidden; text-overflow: ellipsis;
                white-space: nowrap; font-weight: 600; }
            .racer .bar-track { flex: 1; height: 8px; border-radius: 999px; background: #0c0e12;
                border: 1px solid var(--card-border); overflow: hidden; }
            .racer .bar-fill { height: 100%; border-radius: 999px;
                background: linear-gradient(90deg, var(--accent-3), #7cf2c4); transition: width 0.4s ease; }
            .racer.leader .bar-fill { background: linear-gradient(90deg, var(--gold), var(--accent-2)); }
            .racer .cp-text { flex: 0 0 auto; width: 3.6rem; text-align: right; font-size: 0.8rem;
                color: var(--text-dim); font-variant-numeric: tabular-nums; }
            .racer .trophy { flex: 0 0 auto; width: 1.2rem; text-align: center; }

            ol.top-list { margin: 0; padding: 0; list-style: none; counter-reset: rank; }
            ol.top-list li { display: flex; align-items: center; gap: 0.65rem; padding: 0.4rem 0; }
            ol.top-list li + li { border-top: 1px solid var(--card-border); }
            ol.top-list .medal { flex: 0 0 auto; width: 1.6rem; text-align: center; font-weight: 800; }
            ol.top-list li:nth-child(1) .medal { color: var(--gold); }
            ol.top-list li:nth-child(2) .medal { color: var(--silver); }
            ol.top-list li:nth-child(3) .medal { color: var(--bronze); }
            ol.top-list .top-name { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            ol.top-list .top-count { flex: 0 0 auto; color: var(--text-dim); font-size: 0.85rem; }

            .empty { color: var(--text-dim); font-style: italic; font-size: 0.9rem; }
            footer { text-align: center; color: var(--text-faint); font-size: 0.75rem; margin-top: 1.5rem; }

            @media (max-width: 520px) {
                .map-card { flex-direction: column; }
                .map-image-wrap { width: 100%; aspect-ratio: 16 / 9; }
                .racer .name { width: 6.5rem; }
            }
        </style>
        </head>
        <body>
        <main>
            <header>
                <h1 id="server-name">100% TMX Project</h1>
                <div class="badges">
                    <span class="badge" id="preset-badge" style="display:none"></span>
                    <span class="badge" id="session-badge">loading...</span>
                </div>
            </header>

            <div class="card map-card">
                <div class="map-image-wrap" id="map-image-wrap" style="display:none">
                    <img id="map-image" alt="Track preview">
                </div>
                <div class="map-info">
                    <div class="map-name" id="map-name">-</div>
                    <a class="map-link" id="map-link" href="#" target="_blank" rel="noopener">View on TMX</a>
                    <div class="stat-row">
                        <div class="stat">
                            <div class="value" id="player-count">-</div>
                            <div class="label">players online</div>
                        </div>
                        <div class="stat">
                            <div class="value" id="checkpoint-count">-</div>
                            <div class="label">checkpoints</div>
                        </div>
                    </div>
                </div>
            </div>

            <div class="card">
                <h2>Now Racing</h2>
                <div id="racers"><div class="empty">loading...</div></div>
            </div>

            <div class="card">
                <h2>Top Finishers</h2>
                <ol class="top-list" id="top-list"><li class="empty">loading...</li></ol>
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

                    const sessionBadge = document.getElementById('session-badge');
                    if (s.SessionActive) {
                        sessionBadge.className = 'badge live';
                        sessionBadge.innerHTML = '<span class="dot"></span> Live';
                    } else {
                        sessionBadge.className = 'badge stopped';
                        sessionBadge.textContent = 'Session stopped';
                    }

                    const presetBadge = document.getElementById('preset-badge');
                    if (s.PresetDisplayName) {
                        presetBadge.style.display = 'inline-flex';
                        presetBadge.textContent = s.PresetDisplayName;
                    } else {
                        presetBadge.style.display = 'none';
                    }

                    document.getElementById('player-count').textContent = s.PlayerCount ?? '0';
                    document.getElementById('checkpoint-count').textContent = s.CurrentMapCheckpoints ?? '-';
                    document.getElementById('map-name').textContent = s.CurrentMapName || 'No map loaded';

                    const link = document.getElementById('map-link');
                    link.style.display = s.CurrentMapUrl ? 'inline-block' : 'none';
                    if (s.CurrentMapUrl) link.href = s.CurrentMapUrl;

                    const imgWrap = document.getElementById('map-image-wrap');
                    const img = document.getElementById('map-image');
                    if (s.CurrentMapImageUrl) {
                        if (img.src !== s.CurrentMapImageUrl) img.src = s.CurrentMapImageUrl;
                        imgWrap.style.display = 'block';
                    } else {
                        imgWrap.style.display = 'none';
                    }

                    const racers = document.getElementById('racers');
                    if (s.Players && s.Players.length > 0) {
                        const total = s.CurrentMapCheckpoints || 0;
                        racers.innerHTML = s.Players.map((p, i) => {
                            const pct = total > 0 ? Math.min(100, Math.round((p.Checkpoint / total) * 100)) : 0;
                            return `<div class="racer${p.IsLeader ? ' leader' : ''}">
                                <div class="rank">${i + 1}</div>
                                <div class="name">${escapeHtml(p.Nickname)}</div>
                                <div class="bar-track"><div class="bar-fill" style="width:${pct}%"></div></div>
                                <div class="cp-text">${p.Checkpoint}${total ? ' / ' + total : ''}</div>
                                <div class="trophy">${p.IsLeader ? '\u{1F3C6}' : ''}</div>
                            </div>`;
                        }).join('');
                    } else {
                        racers.innerHTML = '<div class="empty">Nobody online right now.</div>';
                    }

                    const list = document.getElementById('top-list');
                    const medals = ['\u{1F947}', '\u{1F948}', '\u{1F949}'];
                    if (s.Top && s.Top.length > 0) {
                        list.innerHTML = s.Top.map((e, i) => `<li>
                            <span class="medal">${medals[i] || (i + 1)}</span>
                            <span class="top-name">${escapeHtml(e.LastNickname)}</span>
                            <span class="top-count">${e.Finishes} finish(es)</span>
                        </li>`).join('');
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
