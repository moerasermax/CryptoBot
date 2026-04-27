namespace CryptoBot.Application.Common;

/// <summary>
/// S70：依使用者本地時區計算「今日 00:00 對應的 UTC 時間」純函式。
///
/// <para>
/// DB 一律存 UTC，但「今日 PnL」「今日成交量」這類語意必須對齊使用者所在時區。
/// 例：UTC+8 使用者眼中的「2026-04-27 今日 00:00」= UTC `2026-04-26 16:00`。
/// 直接用 <c>DateTime.UtcNow.Date</c> 會把昨日後段（本地 08:00 ~ 24:00）算進今日，
/// 對應 S70 Dashboard PnL 顯示誤判事件。
/// </para>
///
/// <para>純函式設計，與 Domain / Infra 解耦，可在 Application.Tests 直接測跨日界。</para>
/// </summary>
public static class LocalDayBoundary
{
    /// <summary>
    /// 取得「<paramref name="utcNow"/> 對應的本地時區當天 00:00」回換成 UTC 的時刻。
    /// </summary>
    /// <param name="utcNow">當下 UTC 時間（呼叫端注入 <see cref="TimeProvider"/> 結果）。</param>
    /// <param name="localTimeZone">使用者所在時區（如 <c>Asia/Taipei</c>）。</param>
    /// <returns>該本地時區「今日 00:00」對應的 UTC 時間。</returns>
    public static DateTime GetDayStartUtc(DateTime utcNow, TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(localTimeZone);

        var utcKind = utcNow.Kind == DateTimeKind.Utc
            ? utcNow
            : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(utcKind, localTimeZone);
        var dayStartLocal = DateTime.SpecifyKind(nowLocal.Date, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(dayStartLocal, localTimeZone);
    }

    /// <summary>
    /// 依時區 ID 字串解析 <see cref="TimeZoneInfo"/>；解析失敗回退至 <c>Asia/Taipei</c>。
    /// 用於從 <c>appsettings.json</c> 讀字串配置時的容錯入口。
    /// </summary>
    public static TimeZoneInfo ResolveTimeZoneOrTaipei(string? tzId)
    {
        const string fallback = "Asia/Taipei";
        var id = string.IsNullOrWhiteSpace(tzId) ? fallback : tzId;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(fallback);
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(fallback);
        }
    }
}
