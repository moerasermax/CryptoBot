using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Ai;

/// <summary>
/// 餵給 AI Advisor 的量化市場摘要。由 <see cref="IMarketContextBuilder"/> 從最近 N 根 K 線計算得出，
/// 讓 AI 不用看完整時序、也能憑幾個關鍵數字判斷當下屬於「上漲趨勢」或「區間震盪」。
/// </summary>
/// <param name="Symbol">市場符號（BingX 格式，例如 BTC-USDT）</param>
/// <param name="Interval">K 線週期</param>
/// <param name="KlineCount">納入計算的 K 線根數</param>
/// <param name="LatestClose">最後一根 K 線的收盤價</param>
/// <param name="Rsi14">RSI(14) 當前值；資料不足時為 null</param>
/// <param name="Atr14">ATR(14) 當前值；資料不足時為 null</param>
/// <param name="BbUpper">布林帶上軌（Period=20, σ=2）</param>
/// <param name="BbMiddle">布林帶中軌</param>
/// <param name="BbLower">布林帶下軌</param>
/// <param name="Ema20">EMA(20) — 簡單趨勢斜率判讀用</param>
/// <param name="Ema50">EMA(50)</param>
/// <param name="HighRecent">最近 20 根最高價</param>
/// <param name="LowRecent">最近 20 根最低價</param>
/// <param name="PercentChange">視窗內首尾收盤價漲跌幅（百分比）</param>
/// <param name="TrendLabel">初步趨勢標籤：Uptrend / Downtrend / Ranging（供 Prompt 加註提示）</param>
/// <param name="BbPositionPercent">當前價在布林帶寬度中的位置（0=下軌、1=上軌）；資料不足為 null</param>
public sealed record MarketContext(
    string Symbol,
    KlineInterval Interval,
    int KlineCount,
    decimal LatestClose,
    decimal? Rsi14,
    decimal? Atr14,
    decimal? BbUpper,
    decimal? BbMiddle,
    decimal? BbLower,
    decimal? Ema20,
    decimal? Ema50,
    decimal HighRecent,
    decimal LowRecent,
    decimal PercentChange,
    TrendLabel TrendLabel,
    decimal? BbPositionPercent);

/// <summary>
/// 初步趨勢分類 — 純靠 EMA 斜率與布林帶寬判斷，給 Prompt 當 hint，AI 仍可覆寫。
/// </summary>
public enum TrendLabel
{
    Unknown,
    Uptrend,
    Downtrend,
    Ranging,
}

public interface IMarketContextBuilder
{
    /// <summary>
    /// 取最近 <paramref name="klineCount"/> 根 K 線（預設 100）並算出 RSI / ATR / BB / EMA 當前值。
    /// 呼叫 <see cref="Common.Interfaces.IExchangeClient.GetKlinesAsync"/> 抓取，因此會受當前
    /// 交易所金鑰 / mode 影響 — AI 建議跟回測環境因此一致。
    /// </summary>
    Task<MarketContext> BuildAsync(
        Symbol symbol, KlineInterval interval, int klineCount = 100, CancellationToken ct = default);
}
