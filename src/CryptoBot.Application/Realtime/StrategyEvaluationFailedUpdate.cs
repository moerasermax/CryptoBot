namespace CryptoBot.Application.Realtime;

/// <summary>
/// S44：策略評估失敗事件 — 滾動決策日誌顯示 [ERROR] 橘色用。
///
/// 發送時機：<c>StrategyExecutor.ProcessKlineAsync</c> 的 catch 區塊 — 即 AnalyzeAsync / 下單 /
/// risk check 任何一環吞到例外時。非破壞性 — 只是讓 UI 看得到「剛剛這根 K 線處理失敗了」，
/// 不影響 consecutive error 計數 / self-stop 判斷（那些仍由原流程處理）。
/// </summary>
public sealed record StrategyEvaluationFailedUpdate(
    Guid StrategyId,
    string StrategyName,
    DateTime OccurredAtUtc,
    string Symbol,
    string ErrorMessage);
