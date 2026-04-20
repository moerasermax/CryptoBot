using CryptoBot.Application.Strategies;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.PositionAggregate;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.ValueObjects;
using Xunit;

namespace CryptoBot.Application.Tests.Strategies;

public class StrategyFactoryTests
{
    [Fact]
    public void Get_KnownType_ReturnsImpl()
    {
        var a = new StubStrategy("TypeA");
        var b = new StubStrategy("TypeB");
        var factory = new StrategyFactory(new IStrategy[] { a, b });

        Assert.Same(a, factory.Get("TypeA"));
        Assert.Same(b, factory.Get("TypeB"));
    }

    [Fact]
    public void Get_UnknownType_ThrowsDomainException()
    {
        var factory = new StrategyFactory(new IStrategy[] { new StubStrategy("OnlyType") });

        var ex = Assert.Throws<DomainException>(() => factory.Get("Nope"));
        Assert.Contains("Nope", ex.Message);
    }

    [Fact]
    public void Ctor_DuplicateStrategyType_ThrowsDomainException()
    {
        var dup1 = new StubStrategy("Same");
        var dup2 = new StubStrategy("Same");

        var ex = Assert.Throws<DomainException>(() => new StrategyFactory(new IStrategy[] { dup1, dup2 }));
        Assert.Contains("Same", ex.Message);
    }

    [Fact]
    public void Ctor_EmptyRegistrations_AllGetsThrow()
    {
        var factory = new StrategyFactory(Array.Empty<IStrategy>());
        Assert.Throws<DomainException>(() => factory.Get("Anything"));
    }

    private sealed class StubStrategy : IStrategy
    {
        public StubStrategy(string type) => StrategyType = type;
        public string StrategyType { get; }

        public Task<TradingSignal> AnalyzeAsync(
            StrategyConfiguration config,
            IReadOnlyList<Kline> klines,
            MarketSnapshot snapshot,
            IReadOnlyList<Position> openPositions,
            CancellationToken ct = default) =>
            Task.FromResult(TradingSignal.None(config.Symbol, Price.Create(100m)));
    }
}
