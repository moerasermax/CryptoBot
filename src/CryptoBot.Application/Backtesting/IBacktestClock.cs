using CryptoBot.Domain.Aggregates.MarketDataAggregate;

namespace CryptoBot.Application.Backtesting;

/// <summary>
/// 回測引擎與模擬器之間的「時光推進」介面。
///
/// 設計理由：<see cref="BacktestEngine"/> 位於 Application 層，不該依賴 Infrastructure 的
/// BacktestSimulator 具體型別。透過這個窄介面，Engine 只需要呼叫 <c>AdvanceTo</c> 把當前 K 線
/// 推給模擬器即可；模擬器實作 <see cref="IBacktestClock"/> 與 <see cref="Common.Interfaces.IExchangeClient"/>。
/// </summary>
public interface IBacktestClock
{
    /// <summary>把一根 K 線推進到模擬器，成為後續下單的成交基準。</summary>
    void AdvanceTo(Kline kline);

    /// <summary>在持倉平倉時，由 Engine 把已實現損益回灌虛擬帳戶。</summary>
    void ApplyRealizedPnL(decimal realizedPnL);

    /// <summary>
    /// S32 爆倉檢查：傳入當前未實現損益，若 (虛擬餘額 + 未實現損益) ≤ 0 則視為爆倉，
    /// 將虛擬餘額強制歸零、設置 IsLiquidated 旗標，並回傳 true。引擎應在收到 true 後立即終止迴圈。
    /// </summary>
    bool CheckAndApplyLiquidation(decimal unrealizedPnL);

    /// <summary>S32 爆倉旗標：一旦設為 true，後續 K 線全部略過。</summary>
    bool IsLiquidated { get; }
}
