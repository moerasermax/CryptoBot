using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Realtime;
using CryptoBot.Application.RiskManagement;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Strategies;

/// <summary>
/// 預設的 <see cref="IStrategyExecutorFactory"/> —
/// 把 DI 容器裡的共用服務（資料流、交易所、冷卻追蹤器、scope 工廠、logger 工廠）
/// 注入到 Executor，同時把 per-strategy 的 <see cref="Strategy"/> 與 <see cref="IStrategy"/>
/// 以建構子參數方式傳入。
/// </summary>
public sealed class StrategyExecutorFactory : IStrategyExecutorFactory
{
    private readonly IMarketDataStream _marketData;
    private readonly IExchangeClient _exchange;
    private readonly IStrategyCooldownTracker _cooldownTracker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INotificationService _notifications;
    private readonly IRealtimeBroadcaster _broadcaster;
    private readonly ILoggerFactory _loggerFactory;

    public StrategyExecutorFactory(
        IMarketDataStream marketData,
        IExchangeClient exchange,
        IStrategyCooldownTracker cooldownTracker,
        IServiceScopeFactory scopeFactory,
        INotificationService notifications,
        IRealtimeBroadcaster broadcaster,
        ILoggerFactory loggerFactory)
    {
        _marketData = marketData;
        _exchange = exchange;
        _cooldownTracker = cooldownTracker;
        _scopeFactory = scopeFactory;
        _notifications = notifications;
        _broadcaster = broadcaster;
        _loggerFactory = loggerFactory;
    }

    public IStrategyExecutor Create(Strategy strategy, IStrategy strategyImpl)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(strategyImpl);

        return new StrategyExecutor(
            strategy,
            strategyImpl,
            _marketData,
            _exchange,
            _cooldownTracker,
            _scopeFactory,
            _notifications,
            _broadcaster,
            _loggerFactory.CreateLogger<StrategyExecutor>());
    }
}
