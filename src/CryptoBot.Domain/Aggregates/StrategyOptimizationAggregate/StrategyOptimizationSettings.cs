using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Domain.Aggregates.StrategyOptimizationAggregate;

/// <summary>
/// 策略最佳化設定快照 — S22 引入。
///
/// 用途：不同策略實例（StrategyId）在不同 (Symbol, KlineInterval) 組合上，經過網格搜尋 / 最佳化器
/// 跑出來的「最佳參數組合 + 評分」可在此持久化，下次進入 Lab 頁或重啟系統時直接載回，省一次最佳化。
///
/// 識別語意：複合鍵 (StrategyId, Symbol, KlineInterval) 每組唯一一筆。
/// 同一策略切不同交易對或不同週期要另存一筆 — 這正是 S22 多週期隔離的目的。
///
/// 不是 <see cref="Common.AggregateRoot{TId}"/>：
/// - 沒有單一 Guid Id，識別是複合鍵（Entity&lt;TId&gt; 泛型不支援）。
/// - 無聚合內子實體、無 Domain Events 需求；定位為「設定快照」。
/// 與 HistoricalKlineRecord 同屬「純資料 + 複合主鍵」模式。
///
/// Domain 層純粹性（憲章 §1.2 鐵律 5）：不自動 UtcNow / NewGuid，
/// 呼叫端（Application 層）負責決定 UpdatedAt 時間戳，保證策略可重放可測試。
/// </summary>
public sealed class StrategyOptimizationSettings
{
    /// <summary>對應的策略實例 Id（<c>Strategy</c> Aggregate）。</summary>
    public Guid StrategyId { get; private set; }

    /// <summary>交易對，以 BingX 格式存放（例 "BTC-USDT"）— 與 HistoricalKlineRecord 一致。</summary>
    public string Symbol { get; private set; } = string.Empty;

    /// <summary>K 線週期 — 最佳化的參數僅對這個週期有效。</summary>
    public KlineInterval Interval { get; private set; }

    /// <summary>最佳化器產出的參數組合（以策略自行定義的 JSON schema 序列化）。</summary>
    public string ParametersJson { get; private set; } = string.Empty;

    /// <summary>評分指標（例：Sharpe, Return, CAGR 等），由呼叫端決定語意。</summary>
    public decimal Score { get; private set; }

    /// <summary>最後更新時間（UTC）。呼叫端負責填入，Domain 層不依賴系統時鐘。</summary>
    public DateTime UpdatedAt { get; private set; }

    private StrategyOptimizationSettings() { }

    /// <summary>
    /// 建立一筆最佳化設定快照。
    /// </summary>
    public static StrategyOptimizationSettings Create(
        Guid strategyId,
        Symbol symbol,
        KlineInterval interval,
        string parametersJson,
        decimal score,
        DateTime updatedAtUtc)
    {
        if (strategyId == Guid.Empty)
            throw new DomainException("StrategyId cannot be empty.");
        if (symbol is null)
            throw new DomainException("Symbol cannot be null.");
        if (string.IsNullOrWhiteSpace(parametersJson))
            throw new DomainException("ParametersJson must not be empty.");

        return new StrategyOptimizationSettings
        {
            StrategyId = strategyId,
            Symbol = symbol.BingXFormat,
            Interval = interval,
            ParametersJson = parametersJson,
            Score = score,
            UpdatedAt = updatedAtUtc,
        };
    }

    /// <summary>
    /// 以新的參數 / 評分覆寫現有快照。<paramref name="updatedAtUtc"/> 由呼叫端提供。
    /// </summary>
    public void UpdateOptimization(string parametersJson, decimal score, DateTime updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
            throw new DomainException("ParametersJson must not be empty.");

        ParametersJson = parametersJson;
        Score = score;
        UpdatedAt = updatedAtUtc;
    }
}
