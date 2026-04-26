using System.Globalization;
using System.Text;
using CryptoBot.Application.Ai;
using CryptoBot.ConsoleApp.Lab;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.ConsoleApp.Api;

/// <summary>
/// <c>/api/ai/*</c> — S30 AI 量化導師端點。
///
/// <c>POST /api/ai/advise</c>：
/// <list type="number">
///   <item>用 <see cref="IMarketContextBuilder"/> 從當前交易所抓 K 線 + 跑指標 → <see cref="MarketContext"/></item>
///   <item>用 <see cref="StrategyCatalog"/> 取該策略的合法參數 key 清單</item>
///   <item>呼 <see cref="IAiAdvisorService.GetAdviceAsync"/> 拿 Gemini 回應</item>
/// </list>
/// Service 合約保證不拋 — 任何失敗轉成 <c>Success=false</c> + <c>Error</c>，都回 HTTP 200。
///
/// <c>GET /api/ai/traces</c>（S30-ELITE+）：
/// 回傳最近 N 筆 AI 呼叫的結構化診斷紀錄。每筆含該次呼叫跑過的所有模型嘗試
/// （primary / fallback），帶 HTTP status、finishReason、safetyBlock、錯誤訊息、耗時。
/// 用於 UI 除錯面板，省掉翻 log / 通靈。
/// </summary>
public static class AiAdvisorEndpoints
{
    public static IEndpointRouteBuilder MapAiAdvisorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ai").WithTags("AiAdvisor");

        group.MapPost("/advise", async (
            AiAdviseRequestDto body,
            StrategyCatalog catalog,
            IMarketContextBuilder contextBuilder,
            IAiAdvisorService advisor,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.StrategyKey))
                return Results.BadRequest(new { error = "strategyKey is required." });
            if (string.IsNullOrWhiteSpace(body.Symbol))
                return Results.BadRequest(new { error = "symbol is required." });

            var model = catalog.FindByKey(body.StrategyKey);
            if (model is null)
                return Results.BadRequest(new { error = $"Unknown strategyKey: {body.StrategyKey}" });

            Symbol symbolVo;
            try { symbolVo = Symbol.Parse(body.Symbol); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }

            MarketContext ctx;
            try
            {
                ctx = await contextBuilder.BuildAsync(symbolVo, body.Interval, klineCount: 100, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 抓 K 線失敗（交易所未配置、網路等）— 轉成 Success=false 讓 UI 顯示友善訊息。
                return Results.Ok(new AiAdviseResponseDto(
                    Success: false,
                    Commentary: string.Empty,
                    SuggestedParameters: new Dictionary<string, ParameterGridRange>(),
                    Error: $"無法取得市場資料：{ex.Message}",
                    Model: "gemini",
                    TrendLabel: TrendLabel.Unknown.ToString(),
                    Attempts: Array.Empty<AiAttemptDiagnostic>()));
            }

            var currentParams = body.CurrentParameters ?? new Dictionary<string, decimal>();

            var req = new AiAdviceRequest(
                StrategyKey: model.Key,
                StrategyDisplayName: model.DisplayName,
                Context: ctx,
                CurrentParameters: currentParams,
                ExpectedParameterKeys: model.ExpectedParameterKeys);

            var result = await advisor.GetAdviceAsync(req, ct).ConfigureAwait(false);

            return Results.Ok(new AiAdviseResponseDto(
                Success: result.Success,
                Commentary: result.Commentary,
                SuggestedParameters: result.SuggestedParameters,
                Error: result.Error,
                Model: result.Model,
                TrendLabel: ctx.TrendLabel.ToString(),
                Attempts: result.Attempts));
        });

        // S30-ELITE+：診斷紀錄。寫死預設 20 筆，使用者可用 ?limit=N 覆蓋（會被 log 自動 clamp 到容量上限）。
        group.MapGet("/traces", (IAiAdviceTraceLog log, int? limit) =>
        {
            var take = limit ?? 20;
            var recent = log.GetRecent(take);
            return Results.Ok(new AiTracesResponseDto(
                Count: recent.Count,
                Traces: recent));
        });

        // S30-ELITE+2：探測此金鑰當前可用的模型清單。當 Primary 回 404 時，使用者可
        // 照這份清單挑出真正可用的模型名填回 appsettings / 環境變數。
        group.MapGet("/models", async (IAiAdvisorService advisor, CancellationToken ct) =>
        {
            var result = await advisor.ListModelsAsync(ct).ConfigureAwait(false);
            return Results.Ok(new AiModelListDto(
                Success: result.Success,
                Error: result.Error,
                Count: result.Models.Count,
                Models: result.Models));
        });

        // S36-S38 T2：單一 Symbol 的技術面快照。給 AiAdvisorPanel「Prompt 產生器」用 —
        // 只跑本地 MarketContextBuilder，不觸發任何 AI 模型呼叫，使用者拿數據自行丟進任何 LLM。
        // 失敗（Symbol.Parse / K 線取得）轉成 <c>Success=false</c> + <c>Error</c> 回 HTTP 200，
        // 讓 UI 顯示友善訊息而非紅色錯誤頁。
        group.MapGet("/context", async (
            IMarketContextBuilder contextBuilder,
            string symbol,
            KlineInterval? interval,
            CancellationToken ct) =>
        {
            var iv = interval ?? KlineInterval.OneHour;
            if (string.IsNullOrWhiteSpace(symbol))
                return Results.Ok(MarketSweepSnapshotDto.Failed("", iv, "symbol is required."));

            Symbol sym;
            try { sym = Symbol.Parse(symbol); }
            catch (DomainException ex) { return Results.Ok(MarketSweepSnapshotDto.Failed(symbol, iv, ex.Message)); }

            try
            {
                var ctx = await contextBuilder.BuildAsync(sym, iv, klineCount: 100, ct).ConfigureAwait(false);
                return Results.Ok(MarketSweepSnapshotDto.From(ctx));
            }
            catch (Exception ex)
            {
                return Results.Ok(MarketSweepSnapshotDto.Failed(symbol, iv, ex.Message));
            }
        });

        // S32-S35-REVISED T3：「橫掃 Top 10 市場機會」。拉 10 個主流幣種的最新技術面快照，
        // 組成一份 copy-paste 就能丟到任何 LLM 的分析 Prompt；不呼叫自家 Gemini（用戶可能想餵到
        // 別的模型對照、或單純複製下來人工分析）。Interval 預設 OneHour，可被 UI 覆蓋。
        group.MapGet("/market-sweep", async (
            IMarketContextBuilder contextBuilder,
            KlineInterval? interval,
            CancellationToken ct) =>
        {
            var iv = interval ?? KlineInterval.OneHour;
            var snapshots = new List<MarketSweepSnapshotDto>(TopMarketSweepSymbols.Length);

            foreach (var s in TopMarketSweepSymbols)
            {
                ct.ThrowIfCancellationRequested();
                Symbol sym;
                try { sym = Symbol.Parse(s); }
                catch (DomainException) { continue; }

                try
                {
                    var ctx = await contextBuilder.BuildAsync(sym, iv, klineCount: 100, ct)
                        .ConfigureAwait(false);
                    snapshots.Add(MarketSweepSnapshotDto.From(ctx));
                }
                catch (Exception ex)
                {
                    // 某個幣抓不到 K 線不該拖垮整份報告；標成 Error 讓使用者知道該筆缺。
                    snapshots.Add(MarketSweepSnapshotDto.Failed(s, iv, ex.Message));
                }
            }

            var prompt = BuildMarketSweepPrompt(iv, snapshots);
            return Results.Ok(new MarketSweepResponseDto(
                Interval: iv,
                GeneratedAtUtc: DateTime.UtcNow,
                Snapshots: snapshots,
                Prompt: prompt));
        });

        return app;
    }

    /// <summary>
    /// S32-S35-REVISED T3：橫掃目標幣種清單。與 Lab UI 的 `_topSymbols` 保持同步（PM 指定的 10 支）。
    /// 兩邊各自硬編，允許後續彼此獨立演進（例如 Lab 加可自訂，但橫掃保留「標竿 10 支」語意）。
    /// </summary>
    private static readonly string[] TopMarketSweepSymbols = new[]
    {
        "BTC-USDT", "ETH-USDT", "SOL-USDT", "BNB-USDT", "XRP-USDT",
        "DOGE-USDT", "ADA-USDT", "AVAX-USDT", "DOT-USDT", "LINK-USDT",
    };

    private static string BuildMarketSweepPrompt(
        KlineInterval interval, IReadOnlyList<MarketSweepSnapshotDto> snapshots)
    {
        var sb = new StringBuilder();
        sb.Append("你是一位頂尖的加密貨幣量化交易分析師。以下是當前 ")
          .Append(interval)
          .Append(" 週期下 Top 10 主流幣種的技術面快照，請針對每支：\n");
        sb.AppendLine("1. 用一句話定性目前市場狀態（Trending up / Trending down / Ranging）。");
        sb.AppendLine("2. 給出 30 天內的「短線機會等級」（A / B / C，A 最強）與簡短理由。");
        sb.AppendLine("3. 如果進場，建議偏 Trend Following 還是 Mean Reversion 風格的策略。");
        sb.AppendLine("最後請排一份『此時最值得掃描參數的 Top 3 幣種』榜單。\n");
        sb.AppendLine("## 市場快照");

        foreach (var s in snapshots)
        {
            if (s.Error is not null)
            {
                sb.Append("- ").Append(s.Symbol).Append("：資料取得失敗（")
                  .Append(s.Error).AppendLine("）");
                continue;
            }

            string FmtDec(decimal? v, string fmt = "F2") =>
                v is null ? "—" : v.Value.ToString(fmt, CultureInfo.InvariantCulture);

            sb.Append("- ").Append(s.Symbol)
              .Append("：Close=").Append(s.LatestClose.ToString("F4", CultureInfo.InvariantCulture))
              .Append(", Δ=").Append(s.PercentChange.ToString("F2", CultureInfo.InvariantCulture)).Append('%')
              .Append(", RSI14=").Append(FmtDec(s.Rsi14))
              .Append(", ATR14=").Append(FmtDec(s.Atr14, "F4"))
              .Append(", EMA20=").Append(FmtDec(s.Ema20, "F4"))
              .Append(", EMA50=").Append(FmtDec(s.Ema50, "F4"))
              .Append(", BB%=").Append(FmtDec(s.BbPositionPercent))
              .Append(", Trend=").Append(s.TrendLabel)
              .AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>
/// /lab 頁面送出的 AI 諮詢請求。CurrentParameters 可為 null — 表單還沒填滿時允許空值。
/// </summary>
public sealed record AiAdviseRequestDto(
    string StrategyKey,
    string Symbol,
    KlineInterval Interval,
    IReadOnlyDictionary<string, decimal>? CurrentParameters);

/// <summary>
/// AI 諮詢回應。Success=false 時 SuggestedParameters 會是空 dict，UI 禁用「填入建議參數」。
/// S30-ELITE+：<see cref="Attempts"/> 夾帶這一次呼叫的模型診斷清單，UI 展開即可看見
/// Primary / Fallback 的 HTTP status、finishReason、safetyBlock、錯誤訊息，不必再翻 console log。
/// </summary>
public sealed record AiAdviseResponseDto(
    bool Success,
    string Commentary,
    IReadOnlyDictionary<string, ParameterGridRange> SuggestedParameters,
    string? Error,
    string Model,
    string TrendLabel,
    IReadOnlyList<AiAttemptDiagnostic> Attempts);

/// <summary>
/// <c>GET /api/ai/traces</c> 回應。直接回 <see cref="AiAdviceTrace"/> 清單 — 它已是 UI 友善濃縮版。
/// </summary>
public sealed record AiTracesResponseDto(
    int Count,
    IReadOnlyList<AiAdviceTrace> Traces);

/// <summary>
/// <c>GET /api/ai/models</c> 回應。失敗（金鑰缺、HTTP error）時 <see cref="Success"/>=false + <see cref="Error"/>，
/// UI 就地顯示訊息；成功時 <see cref="Models"/> 直接渲染為表格。
/// </summary>
public sealed record AiModelListDto(
    bool Success,
    string? Error,
    int Count,
    IReadOnlyList<AiModelInfo> Models);

/// <summary>
/// S32-S35-REVISED T3：單一幣種的市場快照（給 Top 10 橫掃用）。
/// 失敗項（抓 K 線壞掉）以 <see cref="Error"/> 非 null 呈現，其餘數值欄位填預設 — UI/Prompt 會
/// 用 <c>Error is not null</c> 切換呈現，不需擔心 0 被誤當「有效收盤價」。
/// </summary>
public sealed record MarketSweepSnapshotDto(
    string Symbol,
    KlineInterval Interval,
    decimal LatestClose,
    decimal PercentChange,
    decimal? Rsi14,
    decimal? Atr14,
    decimal? Ema20,
    decimal? Ema50,
    decimal? BbPositionPercent,
    string TrendLabel,
    string? Error)
{
    public static MarketSweepSnapshotDto From(MarketContext ctx) => new(
        Symbol: ctx.Symbol,
        Interval: ctx.Interval,
        LatestClose: ctx.LatestClose,
        PercentChange: ctx.PercentChange,
        Rsi14: ctx.Rsi14,
        Atr14: ctx.Atr14,
        Ema20: ctx.Ema20,
        Ema50: ctx.Ema50,
        BbPositionPercent: ctx.BbPositionPercent,
        TrendLabel: ctx.TrendLabel.ToString(),
        Error: null);

    public static MarketSweepSnapshotDto Failed(string symbol, KlineInterval iv, string message) => new(
        Symbol: symbol,
        Interval: iv,
        LatestClose: 0m,
        PercentChange: 0m,
        Rsi14: null,
        Atr14: null,
        Ema20: null,
        Ema50: null,
        BbPositionPercent: null,
        TrendLabel: "Unknown",
        Error: message);
}

/// <summary>
/// <c>GET /api/ai/market-sweep</c> 回應。<see cref="Prompt"/> 已是 copy-paste ready，UI 只需丟進
/// textarea + 「複製」按鈕；<see cref="Snapshots"/> 留給未來可能的結構化渲染用（例如縮圖、指標徽章）。
/// </summary>
public sealed record MarketSweepResponseDto(
    KlineInterval Interval,
    DateTime GeneratedAtUtc,
    IReadOnlyList<MarketSweepSnapshotDto> Snapshots,
    string Prompt);
