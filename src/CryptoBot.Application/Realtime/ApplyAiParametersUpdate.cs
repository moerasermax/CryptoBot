using CryptoBot.Application.Ai;

namespace CryptoBot.Application.Realtime;

/// <summary>
/// S74-C：GlobalAiSidebar 偵測到 AI 回應含 JSON 參數塊後、使用者按「🪄 套用參數至實驗室」時透過
/// <c>DashboardEventBus.ApplyAiParametersRequested</c> 廣播此 payload。<c>BacktestLab</c> 訂閱後
/// 比對 <see cref="TargetStrategyKey"/> 與當前 SelectedModel.Key — 不符時 toast 提示切換策略，
/// 相符（或 null）時呼當前 ParameterForm 的 <c>ApplyGridParametersAsync</c> 灌入網格。
///
/// 結構化 payload 而非 raw JSON：parser 在 sidebar 端先跑（共用 <c>AiAdvicePayloadParser</c>），
/// EventBus 訂閱端不再重複解析，避免雙處維護 wire format。
/// </summary>
public sealed record ApplyAiParametersUpdate(
    string? TargetStrategyKey,
    IReadOnlyDictionary<string, ParameterGridRange> Parameters,
    string Commentary);
