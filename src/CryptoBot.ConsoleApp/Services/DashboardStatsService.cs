using CryptoBot.Application.Common;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Realtime;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using Microsoft.Extensions.Configuration;

namespace CryptoBot.ConsoleApp.Services;

/// <summary>
/// 把「儀表板頂部三卡」所需數字從 DB + 交易所彙總成 <see cref="DashboardStatsUpdate"/>。
///
/// S70（2026-04-27）拆分版：
///   * 「今日 00:00」依使用者本地時區（<c>appsettings.json: Display:LocalTimeZone</c>，
///     預設 <c>Asia/Taipei</c>）計算，避免 UTC 日界與使用者直覺錯位。
///   * <c>TodayRealizedPnL</c> 與 <c>OpenUnrealizedPnL</c> 拆成兩個獨立欄位，UI 卡片
///     分區顯示，不再合併（合併會掩蓋真實狀態，違反 IRON ⑤ 風控透明化）。
///
/// 生命週期：Scoped。<see cref="TimeProvider"/> 注入以利測試以 mock clock 驗跨日界邊界。
/// </summary>
public sealed class DashboardStatsService
{
    private readonly IExchangeClient _exchange;
    private readonly IStrategyRepository _strategyRepo;
    private readonly IPositionRepository _positionRepo;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _localTimeZone;

    public DashboardStatsService(
        IExchangeClient exchange,
        IStrategyRepository strategyRepo,
        IPositionRepository positionRepo,
        IConfiguration config,
        TimeProvider? clock = null)
    {
        _exchange = exchange;
        _strategyRepo = strategyRepo;
        _positionRepo = positionRepo;
        _clock = clock ?? TimeProvider.System;
        _localTimeZone = LocalDayBoundary.ResolveTimeZoneOrTaipei(
            config["Display:LocalTimeZone"]);
    }

    public async Task<DashboardStatsUpdate> GetAsync(CancellationToken ct = default)
    {
        // 不寫死 "USDT" — Demo 模式下 _exchange.QuoteAsset = "VST"，永遠查得到正確的 quote 資產
        var quoteBalance = await _exchange.GetFuturesBalanceAsync(ct: ct).ConfigureAwait(false);

        var openPositions = await _positionRepo.GetOpenPositionsAsync(ct).ConfigureAwait(false);
        var openUnrealized = openPositions.Sum(p => p.UnrealizedPnL);

        var utcNow = _clock.GetUtcNow().UtcDateTime;
        var dayStartUtc = LocalDayBoundary.GetDayStartUtc(utcNow, _localTimeZone);

        var closedToday = await _positionRepo
            .GetClosedPositionsInRangeAsync(dayStartUtc, utcNow, ct)
            .ConfigureAwait(false);
        var todayRealized = closedToday.Sum(p => p.RealizedPnL);

        var running = await _strategyRepo.GetByStatusAsync(StrategyStatus.Running, ct).ConfigureAwait(false);

        return new DashboardStatsUpdate(
            Timestamp: utcNow,
            TotalEquity: quoteBalance + openUnrealized,
            TodayRealizedPnL: todayRealized,
            OpenUnrealizedPnL: openUnrealized,
            ActiveStrategyCount: running.Count);
    }
}
