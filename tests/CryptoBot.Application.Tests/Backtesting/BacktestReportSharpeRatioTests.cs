using CryptoBot.Application.Backtesting;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using Xunit;

namespace CryptoBot.Application.Tests.Backtesting;

/// <summary>
/// S26 T1 — <see cref="BacktestReport.SharpeRatio"/> 年化夏普比率計算的行為驗證。
///
/// 邊界案例：
/// <list type="bullet">
///   <item>空 / 單點 EquityCurve → 0（資料不足）</item>
///   <item>所有點相同 → stddev = 0 → 0（沒波動也沒報酬）</item>
///   <item>穩定遞增 → Sharpe 為大正數（收益高且幾乎無波動 → 高 Sharpe）</item>
///   <item>損益相抵的鋸齒 → Sharpe ≈ 0（mean ≈ 0）</item>
///   <item>年化係數隨 bar 時間間距而變（1h 與 1d 的年化因子不同）</item>
/// </list>
/// </summary>
public class BacktestReportSharpeRatioTests
{
    private static BacktestReport Build(IReadOnlyList<EquityPoint> curve, DateTime? first = null, DateTime? last = null)
        => new(
            TotalKlines: curve.Count,
            SignalsTriggered: 0,
            OrdersFilled: 0,
            StartingBalance: 10_000m,
            EndingBalance: curve.Count > 0 ? curve[^1].Equity : 10_000m,
            PeakEquity: curve.Count > 0 ? curve[^1].Equity : 10_000m,
            MaxDrawdownPercent: 0m,
            FirstKlineTime: first ?? (curve.Count > 0 ? curve[0].TimeUtc : null),
            LastKlineTime: last ?? (curve.Count > 0 ? curve[^1].TimeUtc : null),
            Fills: Array.Empty<Order>(),
            EquityCurve: curve);

    [Fact]
    public void EmptyCurve_ReturnsZero()
    {
        var r = Build(Array.Empty<EquityPoint>());
        Assert.Equal(0m, r.SharpeRatio);
    }

    [Fact]
    public void SinglePoint_ReturnsZero()
    {
        var r = Build(new[] { new EquityPoint(DateTime.UtcNow, 10_000m) });
        Assert.Equal(0m, r.SharpeRatio);
    }

    [Fact]
    public void FlatCurve_ZeroStdDev_ReturnsZero()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var curve = Enumerable.Range(0, 24)
            .Select(i => new EquityPoint(t0.AddHours(i), 10_000m))
            .ToArray();
        var r = Build(curve);
        Assert.Equal(0m, r.SharpeRatio);
    }

    [Fact]
    public void MonotonicGrowth_ProducesHighPositiveSharpe()
    {
        // 每小時 +0.1% 的「完美」等比成長 — 波動極低（僅浮點誤差級別），Sharpe 應為非常大的正數。
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var eq = 10_000m;
        var curve = new List<EquityPoint> { new(t0, eq) };
        for (int i = 1; i < 100; i++)
        {
            eq *= 1.001m;
            curve.Add(new EquityPoint(t0.AddHours(i), eq));
        }
        var r = Build(curve);
        Assert.True(r.SharpeRatio > 10m, $"Expected Sharpe >> 0 for near-deterministic growth, got {r.SharpeRatio}");
    }

    [Fact]
    public void ZigZagSymmetric_NearZeroMean_ReturnsSmallSharpe()
    {
        // +1%, -1% 交替（但為了回到同起點需用 (1+x)(1-x)=1 → 並非完全回到原點，仍有 -x² 漂移）。
        // 這裡改用對等的 +0.5% / -0.498% 讓 mean 更接近 0 — Sharpe 應為「明顯小於 monotonic 情境」的低值。
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var curve = new List<EquityPoint> { new(t0, 10_000m) };
        var eq = 10_000m;
        for (int i = 1; i < 100; i++)
        {
            eq = (i % 2 == 1) ? eq * 1.005m : eq * 0.99502m;
            curve.Add(new EquityPoint(t0.AddHours(i), eq));
        }
        var r = Build(curve);
        Assert.True(Math.Abs(r.SharpeRatio) < 5m, $"Expected |Sharpe| small for symmetric zigzag, got {r.SharpeRatio}");
    }

    [Fact]
    public void AnnualizationFactor_ScalesWithBarInterval()
    {
        // 同樣的相對報酬序列，bar 週期從 1 小時延長到 24 小時（1 天），
        // 年化因子由 sqrt(365.25*24) 變成 sqrt(365.25) — 小時的 Sharpe 應 ≈ sqrt(24) ≈ 4.9 倍於日 Sharpe。
        var returns = new decimal[] { 0.002m, -0.001m, 0.003m, -0.0005m, 0.0015m, -0.0008m, 0.0025m, -0.0012m, 0.0018m, -0.0002m };

        decimal SharpeFor(TimeSpan barSpan)
        {
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var eq = 10_000m;
            var curve = new List<EquityPoint> { new(t0, eq) };
            for (int i = 0; i < returns.Length; i++)
            {
                eq *= 1m + returns[i];
                curve.Add(new EquityPoint(t0 + barSpan * (i + 1), eq));
            }
            return Build(curve).SharpeRatio;
        }

        var sharpeHourly = SharpeFor(TimeSpan.FromHours(1));
        var sharpeDaily  = SharpeFor(TimeSpan.FromDays(1));

        // 兩者同號（報酬序列完全相同）且 hourly 的絕對值應明顯大於 daily（因年化 bar 數多）。
        Assert.True(Math.Sign(sharpeHourly) == Math.Sign(sharpeDaily));
        Assert.True(Math.Abs(sharpeHourly) > Math.Abs(sharpeDaily) * 2m,
            $"hourly={sharpeHourly}, daily={sharpeDaily} — expected hourly ≈ sqrt(24)× daily");
    }

    [Fact]
    public void NegativeEquity_SkipsBadSegments_StillComputes()
    {
        // 第一段從 10000 暴跌至負值（此 prev 仍 > 0，這段 return 合法 = -1.5）。
        // 第二段 prev <= 0 → 該段被忽略，後續需要至少 2 段有效 return 才能算 Sharpe。
        // 這裡建 5 個點，其中中間有 prev <= 0 的段應該被跳過但整體仍能算（剩下有效段 ≥ 2）。
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var curve = new[]
        {
            new EquityPoint(t0.AddHours(0),  10_000m),
            new EquityPoint(t0.AddHours(1),   5_000m),
            new EquityPoint(t0.AddHours(2),   4_500m),
            new EquityPoint(t0.AddHours(3),  -1_000m),  // prev 仍 > 0，return 合法（大虧）
            new EquityPoint(t0.AddHours(4),  -2_000m),  // prev ≤ 0 — 此段跳過
            new EquityPoint(t0.AddHours(5),   1_000m),  // prev ≤ 0 — 此段跳過
        };
        var r = Build(curve);
        // 有效 return 段：[0→1], [1→2], [2→3]；兩段輕微負 + 一段重大負 → Sharpe 非正。
        Assert.True(r.SharpeRatio <= 0m, $"Expected non-positive Sharpe in losing scenario, got {r.SharpeRatio}");
    }
}
