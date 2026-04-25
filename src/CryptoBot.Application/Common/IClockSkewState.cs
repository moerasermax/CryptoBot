namespace CryptoBot.Application.Common;

/// <summary>
/// S66-D：本地時鐘 vs 交易所伺服器時鐘的偏差狀態。
///
/// 由 <c>NtpDriftMonitor</c>（BackgroundService）每 5 分鐘呼叫 <c>IExchangeClient.GetServerTimeAsync()</c>
/// 計算 <c>Offset = ServerTime − LocalTime</c> 並更新本狀態。RiskManager 在每筆下單前讀取，
/// 偏差超過 1000ms 即攔截 — 防止簽章失效或行情數據污染。
///
/// **生命週期 Singleton** —— 跨所有策略 / 風控 / 診斷指令共用同一份偏差快照。
/// </summary>
public interface IClockSkewState
{
    /// <summary>
    /// 最近一次同步測得的偏差。<c>ServerTime − LocalTime</c>：正值代表伺服器領先、負值代表伺服器落後。
    /// 從未同步過時回 <see cref="TimeSpan.Zero"/>，搭配 <see cref="LastSyncedAtUtc"/> == null 判斷。
    /// </summary>
    TimeSpan CurrentOffset { get; }

    /// <summary>
    /// 最近一次成功完成同步的本地 UTC 時間戳。<c>null</c> 代表尚未同步成功（系統剛啟動或連續失敗中）。
    /// </summary>
    DateTime? LastSyncedAtUtc { get; }

    /// <summary>
    /// 是否已成功同步過至少一次。RiskManager 用此判斷「沒同步過 → 不該誤殺下單」的早期啟動窗口。
    /// </summary>
    bool IsSynced { get; }

    /// <summary>NtpDriftMonitor 計算完偏差後呼叫；thread-safe，可從 BG 服務並行更新。</summary>
    void Update(TimeSpan offset, DateTime syncedAtUtc);
}
