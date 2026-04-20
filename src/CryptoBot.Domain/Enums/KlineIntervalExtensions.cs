namespace CryptoBot.Domain.Enums;

/// <summary>
/// <see cref="KlineInterval"/> 的輔助擴充。純枚舉映射，無外部依賴 — 放在 Domain 允許各層共用。
/// </summary>
public static class KlineIntervalExtensions
{
    /// <summary>
    /// 回傳該週期對應的 <see cref="TimeSpan"/>。
    /// 月線採近似 30 天（交易所實際以自然月計，回測遊標推進用 30 天足夠）。
    /// </summary>
    public static TimeSpan ToTimeSpan(this KlineInterval interval) => interval switch
    {
        KlineInterval.OneMinute      => TimeSpan.FromMinutes(1),
        KlineInterval.ThreeMinutes   => TimeSpan.FromMinutes(3),
        KlineInterval.FiveMinutes    => TimeSpan.FromMinutes(5),
        KlineInterval.FifteenMinutes => TimeSpan.FromMinutes(15),
        KlineInterval.ThirtyMinutes  => TimeSpan.FromMinutes(30),
        KlineInterval.OneHour        => TimeSpan.FromHours(1),
        KlineInterval.TwoHours       => TimeSpan.FromHours(2),
        KlineInterval.FourHours      => TimeSpan.FromHours(4),
        KlineInterval.SixHours       => TimeSpan.FromHours(6),
        KlineInterval.EightHours     => TimeSpan.FromHours(8),
        KlineInterval.TwelveHours    => TimeSpan.FromHours(12),
        KlineInterval.OneDay         => TimeSpan.FromDays(1),
        KlineInterval.ThreeDays      => TimeSpan.FromDays(3),
        KlineInterval.OneWeek        => TimeSpan.FromDays(7),
        KlineInterval.OneMonth       => TimeSpan.FromDays(30),
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, null),
    };
}
