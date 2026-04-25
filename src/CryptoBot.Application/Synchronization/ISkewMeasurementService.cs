namespace CryptoBot.Application.Synchronization;

/// <summary>
/// S66-E：本地時鐘 vs 交易所伺服器時鐘的單次偏差量測契約。
///
/// **演算法（Round-trip Mid-point 包夾測量）**：
///   1. <c>localBefore</c> = 本地 UTC（呼叫 GetServerTimeAsync 之前）
///   2. <c>serverTime</c> = 交易所回傳之 UTC
///   3. <c>localAfter</c>  = 本地 UTC（GetServerTimeAsync 回應後）
///   4. <c>localMid = (localBefore + localAfter) / 2</c>
///   5. <c>Offset = serverTime − localMid</c>
///
/// 這個演算法用「本地中點」代替任一端時間戳，能扣掉約半個 round-trip 的網路延遲偏差，
/// 比 raw <c>serverTime - DateTime.UtcNow</c> 精確一個量級。
///
/// **單一資料來源（Single Source of Truth）**：原本 <c>NtpDriftMonitor</c> 與
/// <c>CheckSkewCommand</c> 各自實作一份相同邏輯；S66-E 抽取至此處統一維護，
/// 兩處改呼叫本服務，避免演算法漂移。
/// </summary>
public interface ISkewMeasurementService
{
    /// <summary>
    /// 對交易所執行一次 round-trip 量測，回傳完整測量結果（成功時）。
    /// 失敗時拋例外（網路錯、API 錯等），由呼叫端決定如何處理。
    /// </summary>
    Task<SkewMeasurement> MeasureAsync(CancellationToken ct = default);
}

/// <summary>
/// S66-E：單次包夾測量的完整結果快照。
/// 不含「是否安全」的判斷 —— 那是呼叫端（StartupSkewCheck / RiskManager）的職責，
/// 本 record 只負責提供原始量測數據。
/// </summary>
public sealed record SkewMeasurement(
    DateTime LocalBeforeUtc,
    DateTime ServerTimeUtc,
    DateTime LocalAfterUtc,
    DateTime LocalMidUtc,
    TimeSpan Offset,
    TimeSpan RoundTrip);
