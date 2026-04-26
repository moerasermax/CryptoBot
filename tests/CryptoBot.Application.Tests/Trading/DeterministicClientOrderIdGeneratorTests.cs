using CryptoBot.Application.Trading;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using Xunit;

namespace CryptoBot.Application.Tests.Trading;

/// <summary>
/// S66-A：決定性 ClientOrderId 生成器的行為契約。
/// 核心主張：同樣輸入 → 同樣輸出；輸入任一欄位改動 → 輸出改動。
/// </summary>
public class DeterministicClientOrderIdGeneratorTests
{
    private static readonly Guid StratA = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid StratB = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private static readonly Symbol Btc = Symbol.Parse("BTC-USDT");
    private static readonly Symbol Eth = Symbol.Parse("ETH-USDT");
    private static readonly DateTime T0 = new(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Same_input_produces_identical_output()
    {
        var gen = new DeterministicClientOrderIdGenerator();

        var a = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Long, T0);
        var b = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Long, T0);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_strategy_id_changes_output()
    {
        var gen = new DeterministicClientOrderIdGenerator();

        var a = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Long, T0);
        var b = gen.Generate(StratB, Btc, OrderSide.Buy, PositionSide.Long, T0);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_symbol_changes_output()
    {
        var gen = new DeterministicClientOrderIdGenerator();

        var a = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Long, T0);
        var b = gen.Generate(StratA, Eth, OrderSide.Buy, PositionSide.Long, T0);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_side_changes_output()
    {
        var gen = new DeterministicClientOrderIdGenerator();

        var a = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Long, T0);
        var b = gen.Generate(StratA, Btc, OrderSide.Sell, PositionSide.Long, T0);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_position_side_changes_output()
    {
        var gen = new DeterministicClientOrderIdGenerator();

        var a = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Long, T0);
        var b = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Short, T0);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_close_time_changes_output()
    {
        var gen = new DeterministicClientOrderIdGenerator();

        var a = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Long, T0);
        var b = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Long, T0.AddMinutes(1));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Output_fits_bingx_32_char_limit_and_uses_safe_charset()
    {
        var gen = new DeterministicClientOrderIdGenerator();

        var id = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Long, T0);

        Assert.True(id.Length <= 32, $"ID exceeds BingX 32-char limit: len={id.Length} id={id}");
        Assert.StartsWith("cb_", id);
        // 只允許 [a-z0-9_] — 避免任何交易所對特殊字元敏感
        Assert.Matches("^[a-z0-9_]+$", id);
    }

    [Fact]
    public void Output_format_is_20_characters_exactly()
    {
        var gen = new DeterministicClientOrderIdGenerator();

        var id = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Long, T0);

        // cb_ (3) + strategyPrefix (8) + _ (1) + signalHash (8) = 20
        Assert.Equal(20, id.Length);
    }

    [Fact]
    public void Strategy_id_prefix_is_embedded_for_debug_readability()
    {
        var gen = new DeterministicClientOrderIdGenerator();

        var id = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Long, T0);

        // StratA = 11111111-..., so prefix should be "11111111"
        var expectedPrefix = StratA.ToString("N").Substring(0, 8);
        Assert.Contains(expectedPrefix, id);
    }

    [Fact]
    public void DateTime_kind_ignored_same_ticks_produces_same_id()
    {
        // 即便傳入的 DateTime Kind 不是 Utc（例如 Unspecified），只要 Ticks 相同，
        // 生成器應該視為同一個時間點 — 因為 Kline.CloseTime 可能由各路徑構造。
        var gen = new DeterministicClientOrderIdGenerator();

        var tUtc = new DateTime(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);
        var tUnspec = new DateTime(tUtc.Ticks, DateTimeKind.Unspecified);

        var a = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Long, tUtc);
        var b = gen.Generate(StratA, Btc, OrderSide.Buy, PositionSide.Long, tUnspec);

        Assert.Equal(a, b);
    }
}
