using RandomizerAnywhere.Config;
using System.Net.Http.Json;

namespace RandomizerAnywhere;

internal sealed class DiscordNotifier
{
    private readonly HttpClient http;
    private readonly AppConfig config;

    public DiscordNotifier(HttpClient http, AppConfig config)
    {
        this.http = http;
        this.config = config;
    }

    public Task PostAsync(string content, CancellationToken cancellationToken = default) =>
        PostToAsync(config.DiscordWebhookUrl, content, cancellationToken);

    // falls back to the main webhook if no dedicated "hard map" webhook is configured
    public Task PostHardAsync(string content, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(config.DiscordWebhookUrlHard) ? config.DiscordWebhookUrl : config.DiscordWebhookUrlHard;
        return PostToAsync(url, content, cancellationToken);
    }

    private async Task PostToAsync(string webhookUrl, string content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return;
        }

        try
        {
            using var response = await http.PostAsJsonAsync(webhookUrl, new { content }, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: failed to post Discord notification - {ex.Message}");
        }
    }
}
