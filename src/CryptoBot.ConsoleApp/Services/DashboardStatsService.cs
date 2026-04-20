using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Realtime;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;

namespace CryptoBot.ConsoleApp.Services;

/// <summary>
/// 把「儀表板三張卡」所需數字從 DB + 交易所彙總成 <see cref="DashboardStatsUpdate"/>。
///
/// 為什麼不直接在 Hub / Endpoint 裡寫：
/// - 拉完倉位還要計算 mark-to-market + 加今日已實現，邏輯放在一個 Service 裡方便測試；
/// - API 端與 <see cref="DashboardPushService"/> 都要用，抽成可注入的 Scoped 服務避免重複。
///
/// 生命週期：Scoped（因為用到 Scoped 的 <see cref="IPositionRepository"/> / <see cref="IStrategyRepository"/>）。
/// 呼叫端負責自己開 scope。
/// </summary>
public sealed class DashboardStatsService
{
    private readonly IExchangeClient _exchange;
    private readonly IStrategyRepository _strategyRepo;
    private readonly IPositionRepository _positionRepo;

    public DashboardStatsService(
        IExchangeClient exchange,
        IStrategyRepository strategyRepo,
        IPositionRepository positionRepo)
    {
        _exchange = exchange;
        _strategyRepo = strategyRepo;
        _positionRepo = positionRepo;
    }

    public async Task<DashboardStatsUpdate> GetAsync(CancellationToken ct = default)
    {
        var usdtBalance = await _exchange.GetFuturesBalanceAsync("USDT", ct).ConfigureAwait(false);

        var openPositions = await _positionRepo.GetOpenPositionsAsync(ct).ConfigureAwait(false);
        var unrealized = openPositions.Sum(p => p.UnrealizedPnL);

        var dayStart = DateTime.UtcNow.Date;
        var closedToday = await _positionRepo
            .GetClosedPositionsInRangeAsync(dayStart, DateTime.UtcNow, ct)
            .ConfigureAwait(false);
        var realizedToday = closedToday.Sum(p => p.RealizedPnL);

        var running = await _strategyRepo.GetByStatusAsync(StrategyStatus.Running, ct).ConfigureAwait(false);

        return new DashboardStatsUpdate(
            Timestamp: DateTime.UtcNow,
            TotalEquity: usdtBalance + unrealized,
            TodayPnL: realizedToday + unrealized, // 把未實現也算進今日浮盈，避免卡牌靜態
            ActiveStrategyCount: running.Count);
    }
}
