using CryptoBot.Application.Common.Interfaces;

namespace CryptoBot.Application.Notifications;

/// <summary>
/// <see cref="INotificationService"/> 的空實作 — 註冊為預設後備，
/// 即使 Discord / Telegram 未配置，Application 層仍可無條件注入使用。
/// Infrastructure 層若偵測到有效設定會覆蓋此註冊。
/// </summary>
public sealed class NoOpNotificationService : INotificationService
{
    public Task NotifyAsync(string title, string message,
        NotificationLevel level = NotificationLevel.Info,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task NotifyTradeAsync(string symbol, string action,
        decimal price, decimal quantity,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task NotifyErrorAsync(Exception ex, CancellationToken ct = default) => Task.CompletedTask;
}
