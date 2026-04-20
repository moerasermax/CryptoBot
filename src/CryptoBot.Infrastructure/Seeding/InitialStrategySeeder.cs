using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using CryptoBot.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoBot.Infrastructure.Seeding;

/// <summary>
/// 啟動時依 <see cref="StrategySeedOptions"/> 預置一筆策略到 DB。
///
/// 呼叫時機：ConsoleApp 在 <c>Migrate</c> 之後、<c>host.RunAsync</c> 之前。
/// 同名策略已存在時不做事（幂等）。
/// </summary>
public sealed class InitialStrategySeeder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<StrategySeedOptions> _opts;
    private readonly ILogger<InitialStrategySeeder> _logger;

    public InitialStrategySeeder(
        IServiceScopeFactory scopeFactory,
        IOptions<StrategySeedOptions> opts,
        ILogger<InitialStrategySeeder> logger)
    {
        _scopeFactory = scopeFactory;
        _opts = opts;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var cfg = _opts.Value;
        if (!cfg.Enabled)
        {
            _logger.LogInformation("StrategySeed disabled — skipping.");
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var existing = await repo.GetByNameAsync(cfg.Name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogInformation(
                "StrategySeed: strategy {Name} already exists (status={Status}) — skipping.",
                cfg.Name, existing.Status);
            return;
        }

        if (!Enum.TryParse<KlineInterval>(cfg.KlineInterval, ignoreCase: true, out var interval))
            throw new InvalidOperationException(
                $"StrategySeed: invalid KlineInterval '{cfg.KlineInterval}'.");

        var parameters = new Dictionary<string, decimal>
        {
            ["FastSmaPeriod"] = cfg.FastSmaPeriod,
            ["SlowSmaPeriod"] = cfg.SlowSmaPeriod,
        };

        var strategyConfig = StrategyConfiguration.Create(
            symbol: Symbol.Parse(cfg.Symbol),
            interval: interval,
            leverage: Leverage.Create(cfg.Leverage),
            riskPerTradePercent: cfg.RiskPerTradePercent,
            stopLossPercent: cfg.StopLossPercent,
            takeProfitPercent: cfg.TakeProfitPercent,
            maxKlineWindow: cfg.MaxKlineWindow,
            parameters: parameters);

        var strategy = Strategy.Create(cfg.Name, cfg.StrategyType, strategyConfig);
        if (cfg.StartImmediately) strategy.Start();

        await repo.AddAsync(strategy, ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "StrategySeed: created strategy {Name} ({Type}) on {Symbol}@{Interval}, status={Status}.",
            strategy.Name, strategy.StrategyType,
            strategyConfig.Symbol, strategyConfig.Interval, strategy.Status);
    }
}
