using CryptoBot.Domain.Enums;

namespace CryptoBot.Application.Backtesting;

/// <summary>
/// 回測引擎的啟動參數。由上層（CLI / HTTP / 測試）填入，Engine 只消費不解釋。
/// </summary>
public sealed class BacktestOptions
{
    /// <summary>策略要跑的交易對（以 "BTC-USDT" 這類 BingX 格式指定）</summary>
    public required string Symbol { get; init; }

    /// <summary>K 線週期</summary>
    public required KlineInterval Interval { get; init; }

    /// <summary>回測起點（含，UTC）</summary>
    public required DateTime StartTime { get; init; }

    /// <summary>回測終點（含，UTC）</summary>
    public required DateTime EndTime { get; init; }

    /// <summary>虛擬起始資金（USDT）</summary>
    public decimal InitialBalance { get; init; } = 10_000m;

    /// <summary>
    /// 模擬滑價，以 basis points 表示（1 bp = 0.01%，預設 5 bp = 0.0005）。
    ///
    /// ⚠️ 架構契約：滑價計算**僅在 <c>BacktestSimulator.PlaceOrderAsync</c>（Infrastructure 層）套用**。
    /// <c>BacktestEngine</c>（Application 層）嚴禁二次套用，否則會導致滑價加倍且違反憲章 §1.2 鐵律 2 / §1.4：
    /// 交易所成交細節屬於 <c>IExchangeClient</c> 實作側，不得洩漏到業務編排層。
    ///
    /// 讀取鏈路：<c>BacktestEngine.ExecuteSignalAsync</c> 呼叫 <c>_exchange.PlaceOrderAsync(order)</c>，
    /// Simulator 以 <c>basePrice × (1 ± Bps/10000)</c> 計算 <c>fillPrice</c> 並寫入 <c>Order.AverageFillPrice</c>；
    /// Engine 後續只從 Order 讀回、不自己算。
    /// </summary>
    public decimal SlippageBps { get; init; } = 5m;

    /// <summary>模擬手續費率（Taker），小數形式：0.0005 = 5 bps。</summary>
    public decimal CommissionRate { get; init; } = 0.0005m;

    /// <summary>
    /// 遞給策略 <c>AnalyzeAsync</c> 的歷史 K 線視窗長度。
    /// 例：200 代表每次策略評估，看得到截至目前這根為止最近 200 根。
    /// </summary>
    public int WarmupBars { get; init; } = 200;
}
