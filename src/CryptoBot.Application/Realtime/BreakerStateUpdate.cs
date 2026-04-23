namespace CryptoBot.Application.Realtime;

/// <summary>
/// S28 T1：日損熔斷狀態推播 payload。
///
/// <see cref="IsTripped"/> = true 時，UI 必須禁止手動啟動策略並顯示紫色熔斷 banner；
/// false 表示解鎖（跨日自動解除 或 管理員手動 Reset）。
/// </summary>
public sealed record BreakerStateUpdate(
    bool IsTripped,
    DateTime? TrippedAtUtc,
    string? Reason);
