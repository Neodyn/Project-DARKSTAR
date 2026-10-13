using System.Text;
using System.Text.Json;

namespace Darkstar;

/// <summary>
/// Sends status notifications (bot started, SRS connection lost/restored, shutdown, ...) to a
/// Discord channel via an incoming webhook - no bot account, no extra library needed, just an
/// HTTP POST. Mainly useful once the bot runs unattended as a Windows Service, so an outage
/// doesn't go unnoticed until someone tries to use the radio and gets no answer.
///
/// Entirely opt-in: disabled unless DiscordEnabled=true in config.json. Even then, a missing or
/// obviously-invalid DiscordWebhookUrl is caught here (in addition to the startup validation in
/// AppConfig) so notifications simply stay off with a clear log message instead of failing
/// silently on every send attempt.
///
/// To get a webhook URL: in Discord, go to the target channel's Settings -> Integrations ->
/// Webhooks -> New Webhook, then copy the URL and paste it into config.json as DiscordWebhookUrl.
/// </summary>
public static class DiscordNotifier
{
    private static string? _webhookUrl;
    private static readonly HttpClient Http = new();

    /// <summary>
    /// Call once at startup with the values from config.json. Notifications only become active
    /// when enabled=true AND webhookUrl is a non-empty, plausible-looking Discord webhook URL -
    /// any other combination is logged and leaves notifications disabled.
    /// </summary>
    public static void Init(bool enabled, string? webhookUrl)
    {
        if (!enabled)
        {
            _webhookUrl = null;
            return;
        }

        var trimmedUrl = string.IsNullOrWhiteSpace(webhookUrl) ? null : webhookUrl.Trim();

        if (trimmedUrl == null)
        {
            Logger.Log("[Discord] DiscordEnabled is true, but DiscordWebhookUrl is empty - notifications stay disabled.");
            _webhookUrl = null;
            return;
        }

        var looksValid = trimmedUrl.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) ||
                          trimmedUrl.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase);
        if (!looksValid)
        {
            Logger.Log($"[Discord] DiscordEnabled is true, but DiscordWebhookUrl ('{trimmedUrl}') doesn't look like a " +
                       "valid Discord webhook URL - notifications stay disabled rather than repeatedly failing.");
            _webhookUrl = null;
            return;
        }

        _webhookUrl = trimmedUrl;
        Logger.Log("[Discord] Status notifications are enabled.");
    }

    public static bool Enabled => _webhookUrl != null;

    /// <summary>
    /// Fire-and-forget notification - never throws, never blocks or slows down the caller.
    /// Does nothing if Discord notifications aren't enabled/configured.
    /// </summary>
    public static void Notify(string message)
    {
        if (_webhookUrl == null) return;
        _ = SendAsync(_webhookUrl, message);
    }

    private static async Task SendAsync(string webhookUrl, string message)
    {
        try
        {
            // Discord webhooks accept a simple {"content": "..."} JSON body for a plain text message.
            var payload = JsonSerializer.Serialize(new { content = message });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await Http.PostAsync(webhookUrl, content);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Logger.Log($"[Discord] Failed to send notification ({(int)response.StatusCode}): {body}");
            }
        }
        catch (Exception ex)
        {
            // A failed Discord notification must never crash or block the bot - it's a
            // best-effort side channel, not a critical path.
            Logger.Log($"[Discord] Failed to send notification: {ex.Message}");
        }
    }
}
