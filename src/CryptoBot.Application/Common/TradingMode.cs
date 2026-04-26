namespace CryptoBot.Application.Common;

/// <summary>
/// 全域交易模式 — 唯一決定「用真錢還是模擬金」的開關。
/// Demo 對應 BingX 的 VST 模擬資產，Live 對應真實 USDT。
///
/// 這個型別放在 Application 層而非 Infrastructure，因為：
/// - <see cref="Interfaces.IExchangeClient.ReconfigureAsync"/> 等熱切換 API 必須能在不依賴
///   交易所實作的情況下被 Web/UI 層呼叫；
/// - 介面合約應該由 Application 擁有，Infrastructure 只負責實作，避免雙向耦合。
/// </summary>
public enum TradingMode
{
    Demo = 0,
    Live = 1,
}
