using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Synchronization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoBot.Application.Tests.Synchronization;

/// <summary>
/// S66-E：StartupSkewCheck 三條健康狀態分支 + 量測失敗。
/// </summary>
public class StartupSkewCheckTests
{
    [Theory]
    [InlineData(0,    SkewStatus.Safe)]
    [InlineData(100,  SkewStatus.Safe)]
    [InlineData(-100, SkewStatus.Safe)]
    [InlineData(500,  SkewStatus.Safe)]    // 邊界：剛好 500ms 仍 Safe（嚴格 > 500 才 Warning）
    [InlineData(501,  SkewStatus.Warning)]
    [InlineData(800,  SkewStatus.Warning)]
    [InlineData(-999, SkewStatus.Warning)]
    [InlineData(1000, SkewStatus.Warning)] // 邊界：剛好 1000ms 仍 Warning（嚴格 > 1000 才 Unsafe）
    [InlineData(1001, SkewStatus.Unsafe)]
    [InlineData(2000, SkewStatus.Unsafe)]
    [InlineData(-2000, SkewStatus.Unsafe)]
    public async Task StatusBoundaries_BehaveAsExpected(long offsetMs, SkewStatus expected)
    {
        var sut = BuildSut(offsetMs);
        var result = await sut.RunAsync();

        Assert.Equal(expected, result.SkewStatus);
        Assert.Equal(offsetMs, result.OffsetMs);
        Assert.True(result.MeasurementSucceeded);
    }

    [Fact]
    public async Task UnsafeStatus_HasActionAdvice()
    {
        var sut = BuildSut(offsetMs: 1500);
        var result = await sut.RunAsync();

        Assert.Equal(SkewStatus.Unsafe, result.SkewStatus);
        Assert.NotNull(result.ActionAdvice);
        Assert.Contains("w32tm", result.ActionAdvice);
    }

    [Fact]
    public async Task WarningStatus_AdviceIncludesDistanceToReject()
    {
        var sut = BuildSut(offsetMs: 800);
        var result = await sut.RunAsync();

        Assert.Equal(SkewStatus.Warning, result.SkewStatus);
        Assert.NotNull(result.ActionAdvice);
        // 800ms → 距 1000ms 邊界 200ms
        Assert.Contains("200", result.ActionAdvice);
    }

    [Fact]
    public async Task SafeStatus_NoActionAdvice()
    {
        var sut = BuildSut(offsetMs: 100);
        var result = await sut.RunAsync();

        Assert.Equal(SkewStatus.Safe, result.SkewStatus);
        Assert.Null(result.ActionAdvice);
    }

    [Fact]
    public async Task MeasurementFailure_ReturnsMeasurementFailedStatus()
    {
        var exchange = new ServerTimeStubExchange(throwOnGet: new InvalidOperationException("network down"));
        var measurement = new SkewMeasurementService(exchange);
        var sut = new StartupSkewCheck(measurement, exchange, NullLogger<StartupSkewCheck>.Instance);

        var result = await sut.RunAsync();

        Assert.Equal(SkewStatus.MeasurementFailed, result.SkewStatus);
        Assert.False(result.MeasurementSucceeded);
        Assert.Null(result.OffsetMs);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("network down", result.ErrorMessage);
    }

    [Fact]
    public async Task AbsoluteOffsetMs_HelperReturnsAbsoluteValue()
    {
        var negSut = BuildSut(offsetMs: -800);
        var negResult = await negSut.RunAsync();
        Assert.Equal(800, negResult.AbsoluteOffsetMs);

        var posSut = BuildSut(offsetMs: 800);
        var posResult = await posSut.RunAsync();
        Assert.Equal(800, posResult.AbsoluteOffsetMs);
    }

    [Fact]
    public async Task AbsoluteOffsetMs_NullWhenMeasurementFailed()
    {
        var exchange = new ServerTimeStubExchange(throwOnGet: new InvalidOperationException("down"));
        var measurement = new SkewMeasurementService(exchange);
        var sut = new StartupSkewCheck(measurement, exchange, NullLogger<StartupSkewCheck>.Instance);

        var result = await sut.RunAsync();

        Assert.Null(result.AbsoluteOffsetMs);
    }

    // ============== Helper ==============

    private static StartupSkewCheck BuildSut(long offsetMs)
    {
        // 用包夾測量出 offsetMs 的方式：local mid 為 t0，server 為 t0 + offsetMs
        var t0 = new DateTime(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);
        var clock = new SteppingClock(t0, t0); // 中點 = t0（before == after，測量 round-trip 0）
        var exchange = new ServerTimeStubExchange(serverTime: t0.AddMilliseconds(offsetMs));
        var measurement = new SkewMeasurementService(exchange, clock);
        return new StartupSkewCheck(measurement, exchange, NullLogger<StartupSkewCheck>.Instance);
    }
}
