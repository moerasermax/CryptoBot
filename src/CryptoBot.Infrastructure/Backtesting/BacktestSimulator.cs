using CryptoBot.Application.Backtesting;
using CryptoBot.Application.Common;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Infrastructure.Backtesting;

/// <summary>
/// 以記憶體模擬的「交易所」：實作 <see cref="IExchangeClient"/>，讓策略代碼完全無感。
///
/// 成交模型（簡化但可重現）：
/// - 接單即以當前 K 線的收盤價為基準價，加減 <c>SlippageBps</c> 得到虛擬成交價。
/// - Buy 視為吃 ask：fill = close × (1 + slippageBps/10000)
/// - Sell 視為吃 bid：fill = close × (1 - slippageBps/10000)
/// - Market 全數成交；Limit/Stop 在骨架版本一律視為 Market（下一次迭代再補觸價邏輯）。
///
/// 記帳模型：
/// - 單一 USDT 虛擬餘額，成交即扣除 notional × commissionRate 作為手續費。
/// - 本版不模擬槓桿保證金 / 強平 / 資金費率 — 這些屬於策略 P&amp;L 精度提升的下一輪工作。
///
/// 與 <see cref="BacktestEngine"/> 的耦合點只有 <see cref="AdvanceTo"/>：引擎在每次策略評估前推一根 K 線進來，
/// 這一刻的 <see cref="CurrentKline"/> 就是 <c>PlaceOrderAsync</c> 的成交依據。
/// </summary>
public sealed class BacktestSimulator : IExchangeClient, IBacktestClock
{
    private readonly BacktestOptions _options;
    private readonly ILogger<BacktestSimulator> _logger;

    /// <summary>回測期間累積的所有已成交訂單，供報告 / 測試斷言使用。</summary>
    private readonly List<Order> _fills = new();

    private Kline? _currentKline;

    public string ExchangeName => "Backtest";
    public string QuoteAsset => "USDT";

    /// <summary>回測一律視為 Demo（不接真實交易所）。</summary>
    public TradingMode CurrentMode => TradingMode.Demo;

    /// <summary>回測沒有真正的「環境」可切，呼叫即 no-op；提供此實作只為滿足介面合約。</summary>
    public Task ReconfigureAsync(TradingMode newMode, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>目前虛擬餘額（USDT），由成交即時扣帳。</summary>
    public decimal VirtualBalance { get; private set; }

    /// <summary>
    /// S32 爆倉旗標。一旦 <see cref="CheckAndApplyLiquidation"/> 回傳 true，此值永遠為 true，
    /// 虛擬餘額會被強制歸零；BacktestEngine 會據此中止回測主迴圈，避免用已歸零的帳戶繼續下單。
    /// </summary>
    public bool IsLiquidated { get; private set; }

    public IReadOnlyList<Order> FilledOrders => _fills;

    public Kline? CurrentKline => _currentKline;

    public BacktestSimulator(BacktestOptions options, ILogger<BacktestSimulator> logger)
    {
        _options = options;
        _logger = logger;
        VirtualBalance = options.InitialBalance;
    }

    /// <summary>由 Engine 呼叫：推進一根 K 線到模擬器，作為下一次下單的成交基準。</summary>
    public void AdvanceTo(Kline kline) => _currentKline = kline;

    /// <summary>
    /// 結算一筆已實現損益到虛擬餘額。由 BacktestEngine 在持倉平倉時呼叫。
    /// Simulator 本身只負責成交手續費，Position P&amp;L 由 Engine 側計算後回灌。
    /// </summary>
    public void ApplyRealizedPnL(decimal realizedPnL) => VirtualBalance += realizedPnL;

    /// <summary>
    /// S32-T1 爆倉核心判斷：當權益（虛擬餘額 + 未實現損益）≤ 0 即判定爆倉。
    ///
    /// <para>
    /// 執行後果：虛擬餘額強制歸零、<see cref="IsLiquidated"/> 設為 true。回傳 true 表示本輪已爆倉，
    /// 上層 <see cref="BacktestEngine"/> 應立即中止主迴圈、不再處理後續 K 線 / 訊號 / 下單。
    /// 已爆倉後再次呼叫一律回傳 true（idempotent），不會回補餘額。
    /// </para>
    ///
    /// <para>
    /// 手續費已於 <see cref="PlaceOrderAsync"/> 扣進 <see cref="VirtualBalance"/>，因此此處的
    /// 「餘額 + 浮動損益」已內含手續費損耗 — 高槓桿下這會加速爆倉。
    /// </para>
    /// </summary>
    public bool CheckAndApplyLiquidation(decimal unrealizedPnL)
    {
        if (IsLiquidated) return true;

        var balBefore = VirtualBalance;
        var equity = balBefore + unrealizedPnL;
        if (equity > 0m) return false;

        VirtualBalance = 0m;
        IsLiquidated = true;
        _logger.LogWarning(
            "💥 [BACKTEST-LIQUIDATION] Equity ≤ 0 (bal={Bal:F4} + uPnL={UPnL:F4} = {Eq:F4}). Balance forced to 0, backtest will halt.",
            balBefore, unrealizedPnL, equity);
        return true;
    }

    // ===== 帳戶 =====

    public Task<decimal> GetFuturesBalanceAsync(string? asset = null, CancellationToken ct = default)
        => Task.FromResult(VirtualBalance);

    public Task<decimal> GetSpotBalanceAsync(string asset, CancellationToken ct = default)
        => Task.FromResult(0m);

    public Task SetLeverageAsync(Symbol symbol, Leverage leverage, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetMarginModeAsync(Symbol symbol, MarginMode mode, CancellationToken ct = default)
        => Task.CompletedTask;

    // ===== 行情 =====

    public Task<IReadOnlyList<Kline>> GetKlinesAsync(
        Symbol symbol,
        KlineInterval interval,
        int limit = 500,
        DateTime? startTime = null,
        DateTime? endTime = null,
        CancellationToken ct = default)
    {
        // 回測情境下，歷史 K 線由 BacktestEngine 從 IHistoricalKlineStore 拉出後餵給策略；
        // 策略本身不該再呼叫 client 取歷史 — 回傳空集合做為安全預設。
        return Task.FromResult<IReadOnlyList<Kline>>(Array.Empty<Kline>());
    }

    public Task<Price> GetMarkPriceAsync(Symbol symbol, CancellationToken ct = default)
        => Task.FromResult(Price.Create(RequireCurrent().Close));

    public Task<Price> GetSpotPriceAsync(Symbol symbol, CancellationToken ct = default)
        => Task.FromResult(Price.Create(RequireCurrent().Close));

    public Task<MarketSnapshot> GetMarketSnapshotAsync(Symbol symbol, CancellationToken ct = default)
    {
        var k = RequireCurrent();
        var mid = Price.Create(k.Close);
        var spread = k.Close * (_options.SlippageBps / 10_000m);
        var bid = Price.Create(k.Close - spread);
        var ask = Price.Create(k.Close + spread);
        return Task.FromResult(MarketSnapshot.Create(symbol, k.OpenTime, mid, bid, ask));
    }

    public Task<SymbolTradingRules> GetTradingRulesAsync(Symbol symbol, CancellationToken ct = default)
        => Task.FromResult(new SymbolTradingRules(
            Symbol: symbol,
            MinQuantity: 0.0001m,
            MaxQuantity: 1_000_000m,
            StepSize: 0.0001m,
            TickSize: 0.01m,
            MinNotional: 1m,
            MaxLeverage: 125));

    // ===== 下單 =====

    public Task PlaceOrderAsync(Order order, CancellationToken ct = default)
    {
        var k = RequireCurrent();

        var slippage = _options.SlippageBps / 10_000m;
        var basePrice = k.Close;
        var fillPrice = order.Side switch
        {
            OrderSide.Buy  => basePrice * (1m + slippage),
            OrderSide.Sell => basePrice * (1m - slippage),
            _ => throw new DomainException($"Unsupported OrderSide: {order.Side}")
        };

        var qty = order.Quantity;
        var notional = fillPrice * qty.Value;
        var commission = notional * _options.CommissionRate;

        // 借用 Order Aggregate 的狀態機記錄成交 — 策略層拿到的 Order 物件行為與真實交易所一致。
        order.AssignExchangeOrderId($"BT-{Guid.NewGuid():N}"[..12]);
        order.RecordFill(qty, Price.Create(fillPrice), commission);

        VirtualBalance -= commission;
        _fills.Add(order);

        _logger.LogInformation(
            "📝 [BACKTEST-FILL] {Time:yyyy-MM-dd HH:mm} {Side} {Qty} {Symbol} @ {Price} (fee={Fee:F4}, bal={Bal:F2})",
            k.OpenTime, order.Side, qty.Value, order.Symbol.BingXFormat, fillPrice, commission, VirtualBalance);

        return Task.CompletedTask;
    }

    public Task CancelOrderAsync(Order order, CancellationToken ct = default)
    {
        if (order.IsActive) order.Cancel("Backtest cancel");
        return Task.CompletedTask;
    }

    public Task RefreshOrderStatusAsync(Order order, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<ExchangePositionInfo>> GetOpenPositionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangePositionInfo>>(Array.Empty<ExchangePositionInfo>());

    private Kline RequireCurrent() => _currentKline
        ?? throw new InvalidOperationException(
            "BacktestSimulator has no current kline; BacktestEngine must call AdvanceTo() before any price / order call.");
}
