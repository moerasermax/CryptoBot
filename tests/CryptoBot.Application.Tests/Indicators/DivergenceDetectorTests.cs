using CryptoBot.Application.Indicators;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Enums;
using Xunit;

namespace CryptoBot.Application.Tests.Indicators;

/// <summary>
/// S63 Phase 2：純邏輯單元測試 — 確保底背離與 PinBar 的數學辨識在各種捏造場景下 100% 正確，
/// 不使用真實 K 線資料以消除「跨 SDK 版本的指標精度漂移」干擾。
/// </summary>
public class DivergenceDetectorTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 簡化 K 線工廠：Open=Close=<paramref name="close"/>、High=Close+0.5、Low 自訂。
    /// 若 <paramref name="low"/> &gt; Close 則被壓到 Close（滿足 Kline.Create 的 low ≤ open/close 驗證）。
    /// </summary>
    private static Kline K(decimal low, decimal close, int idx)
    {
        var open = close;
        var high = close + 0.5m;
        var safeLow = Math.Min(low, Math.Min(open, close));
        var t = Base.AddMinutes(idx * 15);
        return Kline.Create(t, t.AddMinutes(15), open, high, safeLow, close, 1m, KlineInterval.FifteenMinutes);
    }

    private static List<Kline> BuildKlines(
        decimal baseLow, Dictionary<int, decimal> overrides, int count, decimal baseClose = 200m)
    {
        var list = new List<Kline>(count);
        for (int i = 0; i < count; i++)
        {
            var low = overrides.TryGetValue(i, out var v) ? v : baseLow;
            list.Add(K(low, baseClose, i));
        }
        return list;
    }

    private static decimal?[] BuildRsi(
        decimal baseRsi, Dictionary<int, decimal> overrides, int count)
    {
        var arr = new decimal?[count];
        for (int i = 0; i < count; i++)
            arr[i] = overrides.TryGetValue(i, out var v) ? v : baseRsi;
        return arr;
    }

    // ========== Divergence ==========

    [Fact]
    public void Divergence_PriceBreaksLower_RsiHigher_ReturnsTrue()
    {
        // 25 根：pivot L1 @ idx=5 (low=100, rsi=25), pivot L2 @ idx=20 (low=90, rsi=40)
        // 價格破底 (90 < 100)、RSI 抬高 (40 > 25) → 典型底背離
        var klines = BuildKlines(
            baseLow: 150m,
            overrides: new Dictionary<int, decimal> { [5] = 100m, [20] = 90m },
            count: 25);
        var rsi = BuildRsi(
            baseRsi: 50m,
            overrides: new Dictionary<int, decimal> { [5] = 25m, [20] = 40m },
            count: 25);

        Assert.True(PatternDetector.DetectRsiBullishDivergence(klines, rsi));
    }

    [Fact]
    public void Divergence_DoubleBottom_PriceLowerAndRsiLower_ReturnsFalse()
    {
        // 價格破底 + RSI 同步破低（雙破底）— 是弱勢訊號，不是背離，必須回 false
        var klines = BuildKlines(
            150m, new Dictionary<int, decimal> { [5] = 100m, [20] = 90m }, 25);
        var rsi = BuildRsi(
            50m, new Dictionary<int, decimal> { [5] = 25m, [20] = 20m }, 25);

        Assert.False(PatternDetector.DetectRsiBullishDivergence(klines, rsi));
    }

    [Fact]
    public void Divergence_PriceEqual_NotStrictlyLower_ReturnsFalse()
    {
        // 嚴格不等：兩個 pivot low 價格相等 → 不算破底 → false（避免平坦雜訊假訊號）
        var klines = BuildKlines(
            150m, new Dictionary<int, decimal> { [5] = 100m, [20] = 100m }, 25);
        var rsi = BuildRsi(
            50m, new Dictionary<int, decimal> { [5] = 25m, [20] = 40m }, 25);

        Assert.False(PatternDetector.DetectRsiBullishDivergence(klines, rsi));
    }

    [Fact]
    public void Divergence_InsufficientKlines_ReturnsFalse()
    {
        var klines = BuildKlines(150m, new(), 3);
        var rsi = BuildRsi(50m, new(), 3);

        Assert.False(PatternDetector.DetectRsiBullishDivergence(klines, rsi));
    }

    [Fact]
    public void Divergence_OnlyOnePivotConfirmed_ReturnsFalse()
    {
        // pivotSpan=2 時，尾端 2 根不會被判為 pivot — idx=23 的低點因右側只有 1 根確認，不被收錄
        // → 實際僅有 1 個已確認 pivot，< 2 → false
        var klines = BuildKlines(
            150m, new Dictionary<int, decimal> { [5] = 100m, [23] = 80m }, 25);
        var rsi = BuildRsi(
            50m, new Dictionary<int, decimal> { [5] = 25m, [23] = 40m }, 25);

        Assert.False(PatternDetector.DetectRsiBullishDivergence(klines, rsi));
    }

    [Fact]
    public void Divergence_RsiNullAtPivot_SkipsAndReturnsFalseIfLessThanTwo()
    {
        // idx=5 是 pivot 但 RSI 為 null，應被跳過 — 剩下只有 idx=20 一個 pivot → false
        var klines = BuildKlines(
            150m, new Dictionary<int, decimal> { [5] = 100m, [20] = 90m }, 25);
        var rsi = BuildRsi(
            50m, new Dictionary<int, decimal> { [20] = 40m }, 25);
        rsi[5] = null;

        Assert.False(PatternDetector.DetectRsiBullishDivergence(klines, rsi));
    }

    [Fact]
    public void Divergence_NullInputs_ReturnFalseGracefully()
    {
        Assert.False(PatternDetector.DetectRsiBullishDivergence(null!, null!));
    }

    // ========== PinBar ==========

    [Fact]
    public void BullishPinBar_LongLowerShadow_Doji_ReturnsTrue()
    {
        // open=close=101, high=101.5, low=90 → body=0, lowerShadow=11, upperShadow=0.5, range=11.5
        var k = K(low: 90m, close: 101m, idx: 0);
        Assert.True(PatternDetector.IsBullishPinBar(k));
    }

    [Fact]
    public void BullishPinBar_LongLowerShadow_WithSmallBody_ReturnsTrue()
    {
        // body 小、下影長
        var t = Base;
        // open=101, close=101.4, high=101.6, low=90 → body=0.4, lowerShadow=11, upperShadow=0.2, range=11.6
        // lowerShadow(11) >= 2*body(0.8) ✓; >= 0.66*range(7.656) ✓; upperShadow(0.2) <= 0.25*range(2.9) ✓
        var k = Kline.Create(t, t.AddMinutes(15), 101m, 101.6m, 90m, 101.4m, 1m, KlineInterval.FifteenMinutes);
        Assert.True(PatternDetector.IsBullishPinBar(k));
    }

    [Fact]
    public void BullishPinBar_LongUpperShadow_NotBullish()
    {
        // open=close=100, high=120, low=99.5 → upperShadow 長、lowerShadow 短 → 應為 bearish pin，不是 bullish
        var t = Base;
        var k = Kline.Create(t, t.AddMinutes(15), 100m, 120m, 99.5m, 100m, 1m, KlineInterval.FifteenMinutes);

        Assert.False(PatternDetector.IsBullishPinBar(k));
        Assert.True(PatternDetector.IsBearishPinBar(k));
    }

    [Fact]
    public void BullishPinBar_StrongBody_NotPinBar()
    {
        // body 遠大於下影 → 不算 pin bar
        var t = Base;
        // open=100, close=105, high=105.2, low=99.8 → body=5, lowerShadow=0.2
        var k = Kline.Create(t, t.AddMinutes(15), 100m, 105.2m, 99.8m, 105m, 1m, KlineInterval.FifteenMinutes);
        Assert.False(PatternDetector.IsBullishPinBar(k));
    }

    [Fact]
    public void BullishPinBar_ZeroRange_ReturnsFalse()
    {
        // open=high=low=close（不可能在實盤，但邊界保護）— Kline.Create 禁止 high<low，但 open=close=high=low 合法
        var t = Base;
        var k = Kline.Create(t, t.AddMinutes(15), 100m, 100m, 100m, 100m, 1m, KlineInterval.FifteenMinutes);
        Assert.False(PatternDetector.IsBullishPinBar(k));
        Assert.False(PatternDetector.IsBearishPinBar(k));
    }
}
