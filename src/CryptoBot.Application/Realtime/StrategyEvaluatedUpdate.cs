namespace CryptoBot.Application.Realtime;

/// <summary>
/// S42/S44：策略評估心跳事件 — Dashboard「Last Evaluated」時間戳 + 呼吸燈 + 滾動決策日誌 用。
///
/// 發送時機：<c>StrategyExecutor.ProcessKlineAsync</c> 每次呼叫完策略 <c>AnalyzeAsync</c>
/// 後立刻 fire（不論 Signal 是否為 None）— 這代表策略大腦確實在跑，而不是卡在某處。
///
/// 負載維持 primitive 欄位以便 SignalR 序列化，Symbol/Interval/StrategyName 帶上供 UI 分流與顯示。
/// <c>SignalType</c> 為 <see cref="Domain.Enums.SignalType"/> 的字串形式（None/OpenLong/OpenShort/CloseLong/CloseShort）—
/// Dashboard 決策日誌靠這個欄位決定用 INF（None）還是 SIGNAL（其他）色碼。
/// <c>Note</c> 帶上 TradingSignal 的 Reason（如「EMA 金叉 + RSI&lt;70」），供日誌顯示該次決策理由。
/// <c>StrategyType</c> 為當前 executor 綁的大腦型別字串（如 <c>B46RsiBb</c>、<c>SmaCrossover</c>）—
/// S45 熱轉型後，Dashboard 靠這欄位即時把卡片模型標籤刷成新型，不必重拉 <c>/api/strategies</c>。
/// </summary>
public sealed record StrategyEvaluatedUpdate(
    Guid StrategyId,
    string StrategyName,
    DateTime EvaluatedAtUtc,
    string Symbol,
    string Interval,
    decimal LastClosePrice,
    string SignalType,
    string? Note,
    string StrategyType);
