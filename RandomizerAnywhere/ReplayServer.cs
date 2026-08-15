using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace RandomizerAnywhere;

internal sealed class ReplayServer
{
    private readonly string replaysDir;
    private readonly ushort port;

    private WebApplication? app;

    public ReplayServer(string replaysDir, ushort port)
    {
        this.replaysDir = replaysDir;
        this.port = port;
    }

    public async Task StartAsync()
    {
        Directory.CreateDirectory(replaysDir);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        app = builder.Build();
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(replaysDir),
            RequestPath = "/replays",
            ServeUnknownFileTypes = true,
        });

        await app.StartAsync();
    }
}
