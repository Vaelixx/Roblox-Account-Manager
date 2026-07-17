using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RobloxAccountManager.Services;

/// <summary>Fire-and-forget Discord webhook notifications (crash, disconnect, launch fail).</summary>
public static class WebhookService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static bool Configured =>
        !string.IsNullOrWhiteSpace(SettingsService.Current.DiscordWebhookUrl);

    public static async Task SendAsync(string content)
    {
        var url = SettingsService.Current.DiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            string body = JsonSerializer.Serialize(new
            {
                username = "Roblox Account Manager",
                content = content.Length > 1900 ? content[..1900] : content
            });
            using var msg = new StringContent(body, Encoding.UTF8, "application/json");
            await Http.PostAsync(url, msg);
        }
        catch { /* notifications are best-effort */ }
    }

    public static void Notify(string content) => _ = SendAsync(content);
}
