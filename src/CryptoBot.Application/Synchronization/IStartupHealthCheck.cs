using CryptoBot.Application.Common;

namespace CryptoBot.Application.Synchronization;

/// <summary>
/// S66-E：啟動期 Pre-flight 健檢契約。
/// 在 ConsoleApp 完成 DI build 後、HostedService 真正執行前呼叫，
/// 結果交由 ConsoleApp 層渲染 banner（IRON ⑥：Application 層**不碰** Console）。
/// </summary>
public interface IStartupHealthCheck
{
    /// <summary>執行一次同步健檢，回傳結構化結果供 banner 與 abort 邏輯使用。</summary>
    Task<StartupCheckResult> RunAsync(CancellationToken ct = default);
}

/// <summary>S66-E：時鐘漂移的健康狀態枚舉。</summary>
public enum SkewStatus
{
    /// <summary>偏差 ≤ Warning 閾值（500ms）— 可正常運行</summary>
    Safe,
    /// <summary>偏差 介於 Warning 與 Reject 閾值之間（500-1000ms）— 可運行但需校時</summary>
    Warning,
    /// <summary>偏差 > Reject 閾值（1000ms）— RiskManager 會擋所有下單，建議立刻校時或重啟</summary>
    Unsafe,
    /// <summary>量測失敗（網路 / API 錯）— 無法判定，需人工檢查</summary>
    MeasurementFailed,
}

/// <summary>
/// S66-E：啟動健檢結果（純資料 record）。
/// **不包含**渲染邏輯、不包含 abort 決策 —— 那是 ConsoleApp 層的職責。
/// </summary>
public sealed record StartupCheckResult(
    string ExchangeName,
    TradingMode Mode,
    SkewStatus SkewStatus,
    long? OffsetMs,
    long? RoundTripMs,
    string? ActionAdvice,
    string? ErrorMessage)
{
    /// <summary>是否量測成功（可作為 banner 顯示判斷）</summary>
    public bool MeasurementSucceeded => SkewStatus != SkewStatus.MeasurementFailed;

    /// <summary>偏差絕對值（毫秒）—— 用於與 abort 閾值比對；量測失敗時回 null。</summary>
    public long? AbsoluteOffsetMs => OffsetMs.HasValue ? Math.Abs(OffsetMs.Value) : null;
}
