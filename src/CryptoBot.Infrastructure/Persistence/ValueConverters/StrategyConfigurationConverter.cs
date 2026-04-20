using System.Text.Json;
using CryptoBot.Domain.Aggregates.StrategyAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CryptoBot.Infrastructure.Persistence.ValueConverters;

/// <summary>
/// StrategyConfiguration ↔ JSON 字串。
/// 內含 Symbol / Leverage VO 與 Parameters 字典，整體序列化為 JSON column。
/// </summary>
public sealed class StrategyConfigurationConverter : ValueConverter<StrategyConfiguration, string>
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public StrategyConfigurationConverter()
        : base(
            v => Serialize(v),
            v => Deserialize(v))
    {
    }

    private static string Serialize(StrategyConfiguration c)
    {
        var dto = new Dto(
            Symbol: c.Symbol.BingXFormat,
            Interval: c.Interval,
            Leverage: c.Leverage.Value,
            RiskPerTradePercent: c.RiskPerTradePercent,
            StopLossPercent: c.StopLossPercent,
            TakeProfitPercent: c.TakeProfitPercent,
            TrailingStopPercent: c.TrailingStopPercent,
            MaxConcurrentPositions: c.MaxConcurrentPositions,
            Parameters: c.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value));
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    private static StrategyConfiguration Deserialize(string raw)
    {
        var dto = JsonSerializer.Deserialize<Dto>(raw, JsonOpts)
            ?? throw new InvalidOperationException(
                "StrategyConfiguration JSON deserialized to null");

        return StrategyConfiguration.Create(
            symbol: Symbol.Parse(dto.Symbol),
            interval: dto.Interval,
            leverage: Leverage.Create(dto.Leverage),
            riskPerTradePercent: dto.RiskPerTradePercent,
            stopLossPercent: dto.StopLossPercent,
            takeProfitPercent: dto.TakeProfitPercent,
            trailingStopPercent: dto.TrailingStopPercent,
            maxConcurrentPositions: dto.MaxConcurrentPositions,
            parameters: dto.Parameters ?? new Dictionary<string, decimal>());
    }

    private sealed record Dto(
        string Symbol,
        KlineInterval Interval,
        int Leverage,
        decimal RiskPerTradePercent,
        decimal StopLossPercent,
        decimal TakeProfitPercent,
        decimal? TrailingStopPercent,
        int MaxConcurrentPositions,
        Dictionary<string, decimal>? Parameters);
}
