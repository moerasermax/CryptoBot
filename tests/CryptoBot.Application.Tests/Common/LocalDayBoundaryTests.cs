using CryptoBot.Application.Common;
using Xunit;

namespace CryptoBot.Application.Tests.Common;

/// <summary>
/// S70 — <see cref="LocalDayBoundary"/> 跨日界邊界與時區換算驗證。
/// 涵蓋場景：UTC+8 早晨、跨日界前後一秒、本地 12:00 中段、UTC fallback、未知時區回退。
/// </summary>
public class LocalDayBoundaryTests
{
    private static TimeZoneInfo Taipei => TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    [Fact]
    public void GetDayStartUtc_BeforeLocalMidnight_ReturnsPreviousDay()
    {
        // 台灣 04-26 23:59:59 = UTC 04-26 15:59:59 → 「今日」應為台灣 04-26 00:00 = UTC 04-25 16:00
        var utcNow = new DateTime(2026, 4, 26, 15, 59, 59, DateTimeKind.Utc);

        var dayStart = LocalDayBoundary.GetDayStartUtc(utcNow, Taipei);

        Assert.Equal(new DateTime(2026, 4, 25, 16, 0, 0, DateTimeKind.Utc), dayStart);
    }

    [Fact]
    public void GetDayStartUtc_AtLocalMidnight_RollsOverToNewDay()
    {
        // 台灣 04-27 00:00:01 = UTC 04-26 16:00:01 → 「今日」為台灣 04-27 00:00 = UTC 04-26 16:00
        var utcNow = new DateTime(2026, 4, 26, 16, 0, 1, DateTimeKind.Utc);

        var dayStart = LocalDayBoundary.GetDayStartUtc(utcNow, Taipei);

        Assert.Equal(new DateTime(2026, 4, 26, 16, 0, 0, DateTimeKind.Utc), dayStart);
    }

    [Fact]
    public void GetDayStartUtc_LocalMiddayUtcMorning_ReturnsSameLocalDayStart()
    {
        // 使用者貼明細時的場景：台灣 04-27 06:01:18 = UTC 04-26 22:01:18
        // 「今日」應為台灣 04-27 00:00 = UTC 04-26 16:00（不是 UTC 04-26 00:00 的舊邏輯！）
        var utcNow = new DateTime(2026, 4, 26, 22, 1, 18, DateTimeKind.Utc);

        var dayStart = LocalDayBoundary.GetDayStartUtc(utcNow, Taipei);

        Assert.Equal(new DateTime(2026, 4, 26, 16, 0, 0, DateTimeKind.Utc), dayStart);
    }

    [Fact]
    public void GetDayStartUtc_NoonLocal_BoundaryIs16HoursAgo()
    {
        // 台灣 04-27 12:00 = UTC 04-27 04:00 → 「今日」為台灣 04-27 00:00 = UTC 04-26 16:00
        var utcNow = new DateTime(2026, 4, 27, 4, 0, 0, DateTimeKind.Utc);

        var dayStart = LocalDayBoundary.GetDayStartUtc(utcNow, Taipei);

        Assert.Equal(new DateTime(2026, 4, 26, 16, 0, 0, DateTimeKind.Utc), dayStart);
    }

    [Fact]
    public void GetDayStartUtc_UtcTimeZone_EqualsUtcDateBoundary()
    {
        // 配置時區改回 UTC 時，行為應退回原 UTC 日界
        var utcNow = new DateTime(2026, 4, 27, 5, 30, 0, DateTimeKind.Utc);

        var dayStart = LocalDayBoundary.GetDayStartUtc(utcNow, TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc), dayStart);
    }

    [Fact]
    public void GetDayStartUtc_AcceptsUnspecifiedKind_TreatsAsUtc()
    {
        // 健壯性：誤傳 Unspecified Kind 不應拋例外，視為 UTC
        var utcNow = new DateTime(2026, 4, 26, 22, 0, 0, DateTimeKind.Unspecified);

        var dayStart = LocalDayBoundary.GetDayStartUtc(utcNow, Taipei);

        Assert.Equal(new DateTime(2026, 4, 26, 16, 0, 0, DateTimeKind.Utc), dayStart);
    }

    [Fact]
    public void ResolveTimeZoneOrTaipei_NullOrEmpty_ReturnsTaipei()
    {
        Assert.Equal("Asia/Taipei", LocalDayBoundary.ResolveTimeZoneOrTaipei(null).Id);
        Assert.Equal("Asia/Taipei", LocalDayBoundary.ResolveTimeZoneOrTaipei("").Id);
        Assert.Equal("Asia/Taipei", LocalDayBoundary.ResolveTimeZoneOrTaipei("   ").Id);
    }

    [Fact]
    public void ResolveTimeZoneOrTaipei_UnknownId_FallsBackToTaipei()
    {
        // 未知時區字串應 fallback 到 Asia/Taipei，不應拋例外
        var tz = LocalDayBoundary.ResolveTimeZoneOrTaipei("Mars/Olympus_Mons");

        Assert.Equal("Asia/Taipei", tz.Id);
    }

    [Fact]
    public void ResolveTimeZoneOrTaipei_KnownId_ReturnsThatZone()
    {
        var utc = LocalDayBoundary.ResolveTimeZoneOrTaipei("UTC");

        Assert.Equal(TimeZoneInfo.Utc, utc);
    }
}
