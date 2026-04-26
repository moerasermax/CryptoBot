using CryptoBot.Domain.Aggregates.StrategyOptimizationAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace CryptoBot.Infrastructure.Persistence.Repositories;

/// <summary>
/// S22 — 策略最佳化設定倉儲。
///
/// Upsert 語意：以複合鍵 (StrategyId, Symbol, Interval) 去重，存在則 UpdateOptimization，
/// 不存在則 Add。呼叫端負責 <see cref="IUnitOfWork.SaveChangesAsync"/>。
/// </summary>
public sealed class StrategyOptimizationSettingsRepository : IStrategyOptimizationSettingsRepository
{
    private readonly AppDbContext _ctx;

    public StrategyOptimizationSettingsRepository(AppDbContext ctx) => _ctx = ctx;

    public Task<StrategyOptimizationSettings?> GetAsync(
        Guid strategyId, Symbol symbol, KlineInterval interval, CancellationToken ct = default)
    {
        var key = symbol.BingXFormat;
        return _ctx.Set<StrategyOptimizationSettings>()
            .FirstOrDefaultAsync(
                s => s.StrategyId == strategyId && s.Symbol == key && s.Interval == interval,
                ct);
    }

    public async Task<IReadOnlyList<StrategyOptimizationSettings>> GetByStrategyAsync(
        Guid strategyId, CancellationToken ct = default) =>
        await _ctx.Set<StrategyOptimizationSettings>()
            .Where(s => s.StrategyId == strategyId)
            .OrderBy(s => s.Symbol).ThenBy(s => s.Interval)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task UpsertAsync(StrategyOptimizationSettings settings, CancellationToken ct = default)
    {
        var existing = await _ctx.Set<StrategyOptimizationSettings>()
            .FirstOrDefaultAsync(
                s => s.StrategyId == settings.StrategyId
                  && s.Symbol == settings.Symbol
                  && s.Interval == settings.Interval,
                ct).ConfigureAwait(false);

        if (existing is null)
        {
            _ctx.Set<StrategyOptimizationSettings>().Add(settings);
        }
        else
        {
            existing.UpdateOptimization(settings.ParametersJson, settings.Score, settings.UpdatedAt);
        }
    }
}
