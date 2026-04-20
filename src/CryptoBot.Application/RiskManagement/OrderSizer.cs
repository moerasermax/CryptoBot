using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.RiskManagement;

/// <summary>
/// 依據策略風險參數計算下單數量 —
/// 核心公式：<c>qty = (balance × RiskPerTradePercent) / (entryPrice × StopLossPercent)</c>
///
/// 這個公式的物理意涵：
/// 「當價格反向走到止損時，虧損金額恰好等於 balance × RiskPerTradePercent」。
/// 槓桿只影響保證金佔用，不影響這條風險預算 — 所以這裡不乘 leverage。
/// </summary>
public interface IOrderSizer
{
    /// <summary>
    /// 依策略配置與訊號，算出**未裁切**的目標下單數量。
    /// 交易所精度 / 最小下單量 / 名義下限的裁切由呼叫端（通常是 Executor 或下一層轉接器）處理。
    /// </summary>
    Task<Quantity> ComputeAsync(
        Strategy strategy,
        TradingSignal signal,
        CancellationToken ct = default);
}

public sealed class OrderSizer : IOrderSizer
{
    private readonly IExchangeClient _exchange;

    public OrderSizer(IExchangeClient exchange)
    {
        _exchange = exchange;
    }

    public async Task<Quantity> ComputeAsync(
        Strategy strategy,
        TradingSignal signal,
        CancellationToken ct = default)
    {
        var cfg = strategy.Configuration;
        var entry = signal.SuggestedPrice.Value;

        if (entry <= 0)
            throw new DomainException(
                $"Cannot size order — signal suggested price must be positive, got {entry}.");

        var balance = await _exchange.GetFuturesBalanceAsync(ct: ct).ConfigureAwait(false);
        var riskAmount = balance * cfg.RiskPerTradePercent;
        var stopDistance = entry * cfg.StopLossPercent;

        if (stopDistance <= 0)
            throw new DomainException(
                $"Invalid stop distance {stopDistance} for entry {entry} and SL% {cfg.StopLossPercent:P}.");

        var rawQty = riskAmount / stopDistance;

        // 零或負值會讓 Quantity.Create 拋出 — 但業務上「balance 為 0」應該被上層攔下再 log。
        // 這裡至少保證回傳一個非負 Quantity（Quantity.Zero），讓 RiskManager 後續的餘額檢查扛掉錯誤原因。
        return rawQty <= 0 ? Quantity.Zero : Quantity.Create(rawQty);
    }
}
