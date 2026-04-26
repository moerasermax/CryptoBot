using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Trading;

/// <summary>
/// S66-A：決定性 ClientOrderId 生成器。
///
/// 同一個訊號特徵（Strategy + Symbol + Side + PositionSide + Kline.CloseTime）必須永遠
/// 產出**完全相同**的字串。網路逾時或 SDK 自動重試時，BingX 端會以 client-order-id
/// 對齊去重 — 只要 ID 一樣，交易所最多接一筆訂單。
///
/// 介面定義在 Application 層而非 Domain：
///   * Domain 鐵律不允許 <c>DateTime.UtcNow</c>，但 Order 工廠不需要直接呼叫此介面 —
///     Application 編排層先算好 ID 再傳進工廠即可。
///   * 與 <see cref="CryptoBot.Application.Common.Interfaces.IExchangeClient"/> 同層級，
///     都是「Application 編排訂單時需要的能力」。
/// </summary>
public interface IClientOrderIdGenerator
{
    /// <summary>
    /// 依訊號特徵產生決定性的 ClientOrderId。同樣輸入永遠回同樣輸出。
    /// </summary>
    /// <param name="strategyId">下單策略的 Aggregate ID（常數來源）</param>
    /// <param name="symbol">交易標的</param>
    /// <param name="side">Buy / Sell</param>
    /// <param name="positionSide">Long / Short</param>
    /// <param name="signalCloseTimeUtc">觸發訊號之 K 線的 CloseTime（已收盤、決定性）。
    /// **嚴禁傳入 <c>DateTime.UtcNow</c> 或訊息抵達時間** — 那會讓重試的 ID 與首次不同。</param>
    /// <returns>BingX-friendly 字串，長度受實作端控制以符合交易所限制</returns>
    string Generate(
        Guid strategyId,
        Symbol symbol,
        OrderSide side,
        PositionSide positionSide,
        DateTime signalCloseTimeUtc);
}
