using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Fire-and-forget Discord webhook notifications. Supports plain content and rich
/// embeds (coloured card, account thumbnail, fields) for account lifecycle events
/// such as connect / disconnect / reconnect. Everything is best-effort — a failed
/// webhook never surfaces to the caller.
/// </summary>
public static class WebhookService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Discord embed decimal colours.
    public const int ColorGreen  = 0x3FB950; // connected / success
    public const int ColorRed    = 0xE5484D; // disconnected / crash / fail
    public const int ColorBlue   = 0x4F9BFF; // reconnected
    public const int ColorAmber  = 0xE3B341; // warning
    public const int ColorPurple = 0x7B61FF; // in-game / neutral

    public static bool Configured =>
        !string.IsNullOrWhiteSpace(SettingsService.Current.DiscordWebhookUrl);

    // ------------------------------------------------------------------
    //  Plain content (legacy)
    // ------------------------------------------------------------------
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
        catch { /* best-effort */ }
    }

    public static void Notify(string content) => _ = SendAsync(content);

    // ------------------------------------------------------------------
    //  Rich embeds
    // ------------------------------------------------------------------
    /// <summary>Post a single Discord embed. <paramref name="fields"/> is (name, value) pairs, shown inline.</summary>
    public static async Task SendEmbedAsync(string title, string? description, int color,
        string? thumbnailUrl = null, IEnumerable<(string Name, string Value)>? fields = null)
    {
        var url = SettingsService.Current.DiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            var embed = new Dictionary<string, object?>
            {
                ["title"]     = Trim(title, 256),
                ["color"]     = color,
                ["timestamp"] = System.DateTime.UtcNow.ToString("o"),
                ["footer"]    = new { text = "Roblox Account Manager" }
            };
            if (!string.IsNullOrWhiteSpace(description)) embed["description"] = Trim(description!, 2048);
            if (!string.IsNullOrWhiteSpace(thumbnailUrl)) embed["thumbnail"] = new { url = thumbnailUrl };
            if (fields != null)
            {
                var list = new List<object>();
                foreach (var (name, value) in fields)
                {
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    list.Add(new { name = Trim(name, 256), value = Trim(value, 1024), inline = true });
                }
                if (list.Count > 0) embed["fields"] = list;
            }

            string body = JsonSerializer.Serialize(new
            {
                username = "Roblox Account Manager",
                embeds = new[] { embed }
            });
            using var msg = new StringContent(body, Encoding.UTF8, "application/json");
            await Http.PostAsync(url, msg);
        }
        catch { /* best-effort */ }
    }

    // ------------------------------------------------------------------
    //  Account lifecycle helpers
    // ------------------------------------------------------------------
    public static void Connected(Account acc, long placeId, string? jobId)
        => Fire("🟢 Connected", acc.DisplayNameOrUser, ColorGreen, acc.ThumbnailUrl, placeId, jobId);

    public static void Disconnected(string alias, string? thumb, long placeId)
        => Fire("🔴 Disconnected", alias, ColorRed, thumb, placeId, null,
                extra: ("Status", "Client closed or crashed"));

    public static void Reconnected(Account acc, long placeId, string? jobId)
        => Fire("🔁 Reconnected", acc.DisplayNameOrUser, ColorBlue, acc.ThumbnailUrl, placeId, jobId);

    public static void ReconnectFailed(string alias, string? thumb, long placeId, string reason)
        => Fire("❌ Reconnect failed", alias, ColorRed, thumb, placeId, null, extra: ("Reason", reason));

    private static void Fire(string title, string alias, int color, string? thumb,
                             long placeId, string? jobId, (string Name, string Value)? extra = null)
    {
        var fields = new List<(string, string)> { ("Account", alias) };
        if (placeId > 0) fields.Add(("Place", placeId.ToString()));
        if (!string.IsNullOrWhiteSpace(jobId)) fields.Add(("Server", jobId!.Length > 12 ? jobId[..12] + "…" : jobId));
        if (extra is { } e) fields.Add(e);
        _ = SendEmbedAsync(title, null, color, thumb, fields);
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}
