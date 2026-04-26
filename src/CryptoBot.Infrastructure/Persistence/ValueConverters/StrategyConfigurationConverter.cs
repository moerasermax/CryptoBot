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
            // S56 bugfix：CooldownPeriod 與 MaxKlineWindow 先前完全沒寫進 JSON，
            // 導致使用者每次重啟後這兩個值都會變回 Create() 的預設。VCP-3 驗證時
            // 使用者明確說「希望重開系統還是跑存住的」— 加欄位後真正持久化。
            CooldownPeriodTicks: c.CooldownPeriod.Ticks,
            MaxKlineWindow: c.MaxKlineWindow,
            Parameters: c.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value));
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    private static StrategyConfiguration Deserialize(string raw)
    {
        var dto = JsonSerializer.Deserialize<Dto>(raw, JsonOpts)
            ?? throw new InvalidOperationException(
                "StrategyConfiguration JSON deserialized to null");

        // S56 bugfix：舊資料庫的 row 可能沒有 CooldownPeriodTicks / MaxKlineWindow 欄位
        // （升級前序列化的 JSON 沒這兩個 key）— null / 0 都 fallback 成 Create() 預設值，
        // 避免把 Cooldown=0 或 MaxKlineWindow=0 塞進 domain 觸發 < 1 的 validation 例外。
        TimeSpan? cooldown = dto.CooldownPeriodTicks is long t && t > 0
            ? TimeSpan.FromTicks(t)
            : null;
        int? maxKlineWindow = dto.MaxKlineWindow is int w && w >= 1 ? w : null;

        return StrategyConfiguration.Create(
            symbol: Symbol.Parse(dto.Symbol),
            interval: dto.Interval,
            leverage: Leverage.Create(dto.Leverage),
            riskPerTradePercent: dto.RiskPerTradePercent,
            stopLossPercent: dto.StopLossPercent,
            takeProfitPercent: dto.TakeProfitPercent,
            trailingStopPercent: dto.TrailingStopPercent,
            maxConcurrentPositions: dto.MaxConcurrentPositions,
            cooldownPeriod: cooldown,
            maxKlineWindow: maxKlineWindow ?? 200,
            parameters: dto.Parameters ?? new Dictionary<string, decimal>());
    }

    // S56 bugfix：新增 CooldownPeriodTicks（long）與 MaxKlineWindow（int）。都 nullable，
    // 舊 JSON 反序列化時自動為 null，Deserialize 內部 fallback 到 Create 預設。
    private sealed record Dto(
        string Symbol,
        KlineInterval Interval,
        int Leverage,
        decimal RiskPerTradePercent,
        decimal StopLossPercent,
        decimal TakeProfitPercent,
        decimal? TrailingStopPercent,
        int MaxConcurrentPositions,
        long? CooldownPeriodTicks,
        int? MaxKlineWindow,
        Dictionary<string, decimal>? Parameters);
}
