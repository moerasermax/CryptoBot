using System.Net.Http.Json;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoBot.Infrastructure.Notifications;

/// <summary>
/// Discord Webhook 通知實作 — 簡單 HttpClient POST，不走 Discord.Net 客戶端。
///
/// 行為準則：
/// - 任何發送錯誤皆 log+吞例外，不讓通知故障炸掉交易管線。
/// - 顏色依 <see cref="NotificationLevel"/> 切換 embed 顏色帶，Error/Critical 會 @everyone（僅 Critical）。
/// - 未配置 WebhookUrl 或 Enabled=false 時不該被註冊 — DI 在 AddNotifications 內做這個判斷。
/// </summary>
public sealed class DiscordNotificationService : INotificationService
{
    private readonly HttpClient _http;
    private readonly DiscordOptions _opts;
    private readonly ILogger<DiscordNotificationService> _logger;

    public DiscordNotificationService(
        HttpClient http,
        IOptions<DiscordOptions> opts,
        ILogger<DiscordNotificationService> logger)
    {
        _http = http;
        _opts = opts.Value;
        _logger = logger;
    }

    public Task NotifyAsync(string title, string message,
        NotificationLevel level = NotificationLevel.Info,
        CancellationToken ct = default)
        => SendEmbedAsync(title, message, ColorFor(level), level == NotificationLevel.Critical, ct);

    public Task NotifyTradeAsync(string symbol, string action, decimal price, decimal quantity,
        CancellationToken ct = default)
    {
        var title = $"🚀 Trade — {symbol}";
        var body = $"**Action**: {action}\n**Price**: {price}\n**Qty**: {quantity}";
        return SendEmbedAsync(title, body, 0x2ECC71, mention: false, ct);
    }

    public Task NotifyErrorAsync(Exception ex, CancellationToken ct = default)
        => SendEmbedAsync("❌ Bot Error", $"{ex.GetType().Name}: {ex.Message}", 0xE74C3C, mention: false, ct);

    private async Task SendEmbedAsync(string title, string description, int color, bool mention, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.WebhookUrl)) return;

        var payload = new
        {
            content = mention ? "@everyone" : null,
            embeds = new[]
            {
                new
                {
                    title,
                    description,
                    color,
                    timestamp = DateTime.UtcNow.ToString("o"),
                }
            }
        };

        try
        {
            using var resp = await _http.PostAsJsonAsync(_opts.WebhookUrl, payload, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Discord webhook returned {Status} for title='{Title}' — body not retried.",
                    (int)resp.StatusCode, title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord webhook send failed for title='{Title}'.", title);
        }
    }

    private static int ColorFor(NotificationLevel level) => level switch
    {
        NotificationLevel.Info     => 0x3498DB,  // 藍
        NotificationLevel.Warning  => 0xF1C40F,  // 黃
        NotificationLevel.Error    => 0xE67E22,  // 橘
        NotificationLevel.Critical => 0xE74C3C,  // 紅
        _ => 0x95A5A6,
    };
}
