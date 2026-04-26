namespace CryptoBot.Application.Common;

/// <summary>
/// 全域環境（Demo / Live）熱切換的編排介面。
///
/// 這是 S21 的核心安全閥 — 任何 UI / API 想動模式都要走這裡，因為：
/// 1. <b>Stop-First 原則</b>：在動 endpoint 之前必須先把所有 executor 停掉，
///    避免「用前一個環境的訂單算法 + 持倉狀態」打到另一個環境的 endpoint，誤生真錢訂單。
/// 2. <b>原子切換</b>：實作必須序列化呼叫，避免兩個 UI 操作同時切到不同方向造成 mixed state。
/// 3. <b>事件廣播</b>：切完發 <see cref="EnvironmentChanged"/>，UI / 通知 / Audit log 都靠它感知。
///
/// 切完後策略不會自動重啟 — 老闆必須在新環境裡顯式按 Start，等於強制人為再確認一次。
/// </summary>
public interface IEnvironmentSwitcher
{
    /// <summary>當前生效的模式（讀 <see cref="Interfaces.IExchangeClient.CurrentMode"/>）。</summary>
    TradingMode CurrentMode { get; }

    /// <summary>
    /// 執行熱切換流程：StopAll → ExchangeClient.Reconfigure → MarketDataStream.Reconfigure → 重啟 stream → 廣播事件。
    /// 如已是目標模式則為 no-op，但仍會發 <see cref="EnvironmentChanged"/> 讓 UI 校正。
    /// </summary>
    Task<EnvironmentSwitchResult> SwitchAsync(TradingMode newMode, string? reason = null, CancellationToken ct = default);

    /// <summary>切換完成廣播 — 順序：先廣播再 return SwitchAsync，所以訂閱端不會錯過。</summary>
    event Action<EnvironmentChangedEvent>? EnvironmentChanged;
}

/// <summary>切換結果摘要 — UI 需要顯示「停了哪些策略」。</summary>
public sealed record EnvironmentSwitchResult(
    TradingMode FromMode,
    TradingMode ToMode,
    IReadOnlyList<Guid> StoppedStrategyIds,
    DateTime SwitchedAtUtc,
    string? Reason);

/// <summary>
/// 環境變更事件 payload — 含 from/to 與被強制停下的策略清單，方便 UI 標記紅字提示。
/// </summary>
public sealed record EnvironmentChangedEvent(
    TradingMode FromMode,
    TradingMode ToMode,
    IReadOnlyList<Guid> StoppedStrategyIds,
    DateTime ChangedAtUtc,
    string? Reason);
