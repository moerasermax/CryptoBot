using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.RiskManagement;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBot.DiagnosticTool.Commands;

/// <summary>
/// <c>size &lt;entryPrice&gt; &lt;strategyName&gt;</c>：跑一次真實 <see cref="IOrderSizer"/> +
/// <see cref="IRiskManager"/>，印出 Balance → Risk → Leverage → Notional → Final Qty 計算鏈，
/// 並附上交易所 MinQty/StepSize/MinNotional；若 Final Qty 歸零會明確標示是哪個門檻卡關。
/// </summary>
public sealed class SizingCommand : IDiagnosticCommand
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SizingCommand(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string Name => "size";
    public IReadOnlyList<string> Aliases => new[] { "sizing" };
    public string Description => "Simulate order-size pipeline for a strategy and show exchange limits.";
    public string Usage => "size <entryPrice> <strategyName>";

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine($"Usage: {Usage}");
            return 2;
        }

        if (!decimal.TryParse(args[0], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var entryPrice) || entryPrice <= 0)
        {
            Console.Error.WriteLine($"Invalid entry price: {args[0]}");
            return 2;
        }

        var strategyName = args[1];

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var strategyRepo = sp.GetRequiredService<IStrategyRepository>();

        var strategy = await strategyRepo.GetByNameAsync(strategyName, ct);
        if (strategy is null)
        {
            Console.Error.WriteLine($"Strategy not found: '{strategyName}'");
            var all = await strategyRepo.GetAllAsync(ct);
            if (all.Count > 0)
            {
                Console.Error.WriteLine("Available strategies:");
                foreach (var s in all)
                    Console.Error.WriteLine($"  - {s.Name} ({s.StrategyType}, {s.Configuration.Symbol.BingXFormat}@{s.Configuration.Interval})");
            }
            return 3;
        }

        var exchange = sp.GetRequiredService<IExchangeClient>();
        var sizer = sp.GetRequiredService<IOrderSizer>();
        var risk = sp.GetRequiredService<IRiskManager>();

        var cfg = strategy.Configuration;
        var entry = Price.Create(entryPrice);
        var stopLoss = Price.Create(entryPrice * (1m - cfg.StopLossPercent));
        var takeProfit = Price.Create(entryPrice * (1m + cfg.TakeProfitPercent));
        var signal = TradingSignal.OpenLong(
            cfg.Symbol, entry, stopLoss, takeProfit,
            confidence: 1.0m,
            reason: "DiagnosticTool simulation");

        Console.WriteLine("=== CryptoBot Sizing Simulation ===");
        Console.WriteLine($"Strategy:     {strategy.Name} ({strategy.StrategyType})");
        Console.WriteLine($"Symbol:       {cfg.Symbol.BingXFormat} @ {cfg.Interval}");
        Console.WriteLine($"Entry:        {entryPrice}  StopLoss: {stopLoss.Value:F4}  TakeProfit: {takeProfit.Value:F4}");
        Console.WriteLine();

        var balance = await exchange.GetFuturesBalanceAsync(ct: ct);
        var stopDistance = entryPrice * cfg.StopLossPercent;
        var riskAmount = balance * cfg.RiskPerTradePercent;
        var rawQty = stopDistance > 0 ? riskAmount / stopDistance : 0m;
        var leverage = cfg.Leverage.Value;
        var maxNotional = balance * leverage * 0.95m;
        var rawNotional = rawQty * entryPrice;

        Console.WriteLine("-- Calculation chain --");
        Console.WriteLine($"  Balance          : {balance:F4} {exchange.QuoteAsset}");
        Console.WriteLine($"  RiskPerTrade     : {cfg.RiskPerTradePercent:P2}");
        Console.WriteLine($"  StopLossPercent  : {cfg.StopLossPercent:P2} (distance = {stopDistance:F4})");
        Console.WriteLine($"  RiskAmount       : {riskAmount:F4}");
        Console.WriteLine($"  → raw qty        : {rawQty:F6}");
        Console.WriteLine($"  Leverage         : {leverage}x");
        Console.WriteLine($"  MaxNotional(95%) : {maxNotional:F4}");
        Console.WriteLine($"  Raw Notional     : {rawNotional:F4}  {(rawNotional > maxNotional ? "→ CAPPED by margin" : "(within margin)")}");
        Console.WriteLine();

        // S59-ADD T3：診斷工具主動把交易所物理限制印出來 — 讓 PM 一眼判斷「0 歸零」是不是因為
        // 資金太小撐不過 MinQty / MinNotional 門檻。
        SymbolTradingRules? rules = null;
        Console.WriteLine($"-- Exchange rules for {cfg.Symbol.BingXFormat} --");
        try
        {
            rules = await exchange.GetTradingRulesAsync(cfg.Symbol, ct);
            Console.WriteLine($"  MinQty           : {rules.MinQuantity}");
            Console.WriteLine($"  StepSize         : {rules.StepSize}");
            Console.WriteLine($"  MinNotional      : {rules.MinNotional}");
            Console.WriteLine($"  TickSize         : {rules.TickSize}");
            Console.WriteLine($"  MaxLeverage      : {rules.MaxLeverage}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [READ FAIL] {ex.Message}");
        }
        Console.WriteLine();

        Quantity finalQty;
        try
        {
            finalQty = await sizer.ComputeAsync(strategy, signal, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  → Sizer threw  : {ex.GetType().Name}: {ex.Message}");
            return 4;
        }

        Console.WriteLine($"-- Sizer result --");
        Console.WriteLine($"  → Final Qty      : {finalQty.Value:F6}");

        if (finalQty.Value <= 0)
        {
            Console.WriteLine($"  → ZERO (would trigger [SIZE] broadcast at runtime)");
            Console.WriteLine($"  → Root cause     : {DiagnoseZero(balance, rawQty, entryPrice, rules)}");
            Console.WriteLine("RiskManager: SKIPPED (qty==0)");
            return 0;
        }

        var check = await risk.CheckBeforeOpenAsync(strategy, signal, finalQty, ct);
        Console.WriteLine();
        Console.WriteLine("-- RiskManager --");
        Console.WriteLine($"  Approved : {check.IsApproved}");
        if (!check.IsApproved)
            Console.WriteLine($"  Reason   : [RISK] {check.Reason}");
        return 0;
    }

    /// <summary>
    /// 以 OrderSizer 的同樣運算規則複刻一次，精準定位是哪一個門檻把 qty 打到 0 — 這樣 PM 不必
    /// 翻 log 就知道要調大倉位/換資金/換幣種。
    /// </summary>
    private static string DiagnoseZero(decimal balance, decimal rawQty, decimal entryPrice, SymbolTradingRules? rules)
    {
        if (balance <= 0)
            return "帳戶餘額為 0 — 先檢查 env、VST 模擬金或 USDT 實盤餘額。";
        if (rules is null)
            return "交易所規則讀取失敗，無法判斷門檻。";

        var alignedQty = rules.StepSize > 0
            ? Math.Floor(rawQty / rules.StepSize) * rules.StepSize
            : rawQty;

        if (alignedQty < rules.MinQuantity)
            return $"對齊後數量 {alignedQty:F6} < 交易所 MinQty {rules.MinQuantity:F6} — 資金/風險比太小，無法湊到最小下單量。";

        var alignedNotional = alignedQty * entryPrice;
        if (rules.MinNotional > 0 && alignedNotional < rules.MinNotional)
            return $"對齊後名目 {alignedNotional:F4} < 交易所 MinNotional {rules.MinNotional:F4} — 建議提高資金或放寬 StopLoss%。";

        return "未知（請檢查 OrderSizer 的 Warning log）";
    }
}
