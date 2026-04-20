namespace CryptoBot.Application.Realtime;

/// <summary>
/// 已成交交易的即時推播負載。給 Web UI 的「最近交易 Log」用。
///
/// 用 record + primitive types 方便 System.Text.Json / SignalR 序列化，
/// 不要塞 Domain 物件進來（會把 private setter / ValueObject 的 serialization 頭痛帶進 Hub）。
/// </summary>
public sealed record TradeFilledUpdate(
    DateTime Timestamp,
    string Symbol,
    string Side,           // "Buy" / "Sell"
    string PositionSide,   // "Long" / "Short"
    decimal Quantity,
    decimal Price,
    string StrategyName);
