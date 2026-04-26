using System.Text.Json.Serialization;

namespace CryptoBot.Infrastructure.Backtesting.Search;

/// <summary>
/// S69 — Python sidecar JSON 契約 DTOs。**internal** 維持 IRON §⑨ 防腐層 — 不滲透至 Application/Domain。
/// 對應 ai_ops/sidecar/app.py 的 ParameterSpec / StudyCreateRequest / SuggestResponse / TellRequest。
///
/// 字串/數值欄位都用 lowerCamelCase / 與 Pydantic 預設輸出一致；用 [JsonPropertyName] 顯式對齊
/// 避免 .NET 的 PascalCase 預設破壞反序列化。
///
/// 為什麼用 double 而非 decimal：sidecar 端是 float（Python double）— 跨 HTTP JSON 邊界保留
/// double，由 BayesianSearchStrategy 在進入 Application 層前主動轉 decimal（IRON §①）。
/// </summary>
internal sealed record SidecarParameterSpec(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("min")] double Min,
    [property: JsonPropertyName("max")] double Max,
    [property: JsonPropertyName("step")] double Step);

internal sealed record SidecarStudyCreateRequest(
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("parameters")] IReadOnlyList<SidecarParameterSpec> Parameters);

internal sealed record SidecarStudyCreateResponse(
    [property: JsonPropertyName("study_id")] string StudyId);

internal sealed record SidecarSuggestResponse(
    [property: JsonPropertyName("trial_id")] int TrialId,
    [property: JsonPropertyName("parameters")] IReadOnlyDictionary<string, double> Parameters);

internal sealed record SidecarTellRequest(
    [property: JsonPropertyName("trial_id")] int TrialId,
    [property: JsonPropertyName("value")] double Value);
