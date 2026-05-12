namespace CryptoBot.Application.Ai;

/// <summary>
/// AI 建議的單一參數網格範圍。優化掃描會以此產生 ( (Max-Min)/Step + 1 ) 個回測點。
/// Min &lt;= Max 且 Step &gt; 0 由上層（Sanitize / 表單驗證）保證，此 record 本身不驗證。
///
/// S30-GRID 引入；S74-D（legacy AI advisor 移除）抽出為獨立檔，與 IAiAdvisorService.cs 解耦。
/// 仍是 Sidekick (<see cref="IGlobalAiChatService"/>) + 共用 <c>AiAdvicePayloadParser</c> +
/// <c>StrategyParameterFormBase.ApplyGridParametersAsync</c> 三方共同的參數網格序列化型別。
/// </summary>
public sealed record ParameterGridRange(decimal Min, decimal Max, decimal Step);
