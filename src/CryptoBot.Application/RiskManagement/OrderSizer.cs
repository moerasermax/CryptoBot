using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CryptoBot.Application.RiskManagement;

/// <summary>
/// 依據策略風險參數計算下單數量 —
/// 核心公式：<c>qty = (balance × RiskPerTradePercent) / (entryPrice × StopLossPercent)</c>
///
/// 這個公式的物理意涵：
/// 「當價格反向走到止損時，虧損金額恰好等於 balance × RiskPerTradePercent」。
/// 槓桿只影響保證金佔用，不影響這條風險預算 — 所以這裡不乘 leverage。
///
/// S99-T7：除了風險預算，Sizer 自己也扛「實體可交易」的校驗 —
/// 保證金上限、交易所 stepSize / minQuantity / minNotional 一次處理到位，
/// 避免送出必定被 BingX bounce 的訂單。RiskManager 的 ReserveRatio 仍是上層政策，
/// 這裡只管「物理可行」。
/// </summary>
public interface IOrderSizer
{
    /// <summary>
    /// 依策略配置與訊號，算出**可送交易所**的目標下單數量。
    /// 已對齊 stepSize、套過 minQuantity / minNotional 門檻、套過保證金上限。
    /// 不可交易時回傳 <see cref="Quantity.Zero"/>，由呼叫端負責 log + skip。
    /// </summary>
    Task<Quantity> ComputeAsync(
        Strategy strategy,
        TradingSignal signal,
        CancellationToken ct = default);
}

public sealed class OrderSizer : IOrderSizer
{
    /// <summary>
    /// S50 合約：實際可用保證金只取 balance × leverage 的 95%，剩 5% 緩衝留給
    /// 手續費 / 滑價 / mark price 抖動 — 避免 BingX 在下單瞬間回 100010 "Insufficient margin"。
    /// </summary>
    private const decimal MarginBufferFactor = 0.95m;

    private readonly IExchangeClient _exchange;
    private readonly ILogger<OrderSizer> _logger;

    public OrderSizer(IExchangeClient exchange, ILogger<OrderSizer>? logger = null)
    {
        _exchange = exchange;
        _logger = logger ?? NullLogger<OrderSizer>.Instance;
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

        // 永遠讀即時帳戶餘額（Live→BingX REST, Demo→BacktestSimulator 的動態錢包） —
        // 這裡不吃 appsettings / Lab Initial Balance 之類的靜態數字。
        var balance = await _exchange.GetFuturesBalanceAsync(ct: ct).ConfigureAwait(false);
        if (balance <= 0)
        {
            _logger.LogWarning(
                "Sizer: account balance is {Balance} — returning Zero quantity for {Symbol}.",
                balance, signal.Symbol.BingXFormat);
            return Quantity.Zero;
        }

        var stopDistance = entry * cfg.StopLossPercent;
        if (stopDistance <= 0)
            throw new DomainException(
                $"Invalid stop distance {stopDistance} for entry {entry} and SL% {cfg.StopLossPercent:P}.");

        // 1) 風險預算（止損觸發時願意賠的絕對金額）→ 原始數量
        var riskAmount = balance * cfg.RiskPerTradePercent;
        var rawQty = riskAmount / stopDistance;
        if (rawQty <= 0) return Quantity.Zero;

        // 2) 保證金上限 — S50 合約：MaxNotional = balance × leverage × 0.95。
        //    5% 緩衝是留給 BingX 的手續費 / 滑價 / mark price 抖動，避免「剛剛好」的單
        //    在交易所真正報價時被 100010 "Insufficient margin" 打回票。
        //    超過就按 MaxNotional / entry 直接砍到安全上限，並記警告。
        var leverage = cfg.Leverage.Value;
        var maxNotional = balance * leverage * MarginBufferFactor;   // 0.95
        var notional = rawQty * entry;
        if (notional > maxNotional)
        {
            var cappedQty = maxNotional / entry;
            _logger.LogWarning(
                "Sizer: risk-budget qty {RawQty:F6} notional {Notional:F2} > maxNotional {Max:F2} " +
                "(balance {Balance:F2} × lev {Lev}x × {Buf:P0}); capping to {Capped:F6} ({Symbol}).",
                rawQty, notional, maxNotional, balance, leverage, MarginBufferFactor,
                cappedQty, signal.Symbol.BingXFormat);
            rawQty = cappedQty;
        }

        // 3) 交易所規則：對齊 stepSize、驗 minQuantity / minNotional。
        //    任一門檻沒過就回傳 Zero（Executor 會 log "zero quantity — skipping"），
        //    不硬送必定被 bounce 的小單或碎步量。
        var rules = await _exchange.GetTradingRulesAsync(signal.Symbol, ct).ConfigureAwait(false);
        var alignedQty = rules.StepSize > 0
            ? Math.Floor(rawQty / rules.StepSize) * rules.StepSize
            : rawQty;

        if (alignedQty < rules.MinQuantity)
        {
            _logger.LogWarning(
                "Sizer: aligned qty {Aligned:F6} < minQuantity {Min:F6} for {Symbol}; returning Zero.",
                alignedQty, rules.MinQuantity, signal.Symbol.BingXFormat);
            return Quantity.Zero;
        }

        var alignedNotional = alignedQty * entry;
        if (rules.MinNotional > 0 && alignedNotional < rules.MinNotional)
        {
            _logger.LogWarning(
                "Sizer: aligned notional {Notional:F4} < minNotional {Min:F4} for {Symbol}; returning Zero.",
                alignedNotional, rules.MinNotional, signal.Symbol.BingXFormat);
            return Quantity.Zero;
        }

        return Quantity.Create(alignedQty);
    }
}
