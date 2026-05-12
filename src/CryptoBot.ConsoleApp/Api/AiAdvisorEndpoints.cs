using System.Globalization;
using System.Text;
using CryptoBot.Application.Ai;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;

namespace CryptoBot.ConsoleApp.Api;

/// <summary>
/// <c>/api/ai/*</c> — AI 相關端點。
///
/// S74-D：legacy <c>POST /api/ai/advise</c> + <c>GET /api/ai/traces</c> + <c>GET /api/ai/models</c>
/// 已隨 <c>IAiAdvisorService</c> 一併移除（CryptoBot Sidekick 全域 sidebar 取代）。剩餘端點：
///
/// <list type="bullet">
///   <item><c>GET /api/ai/config</c>：暴露當前 advisor provider 名稱（向下兼容用，下游 UI 可能讀）。</item>
///   <item><c>GET /api/ai/context</c>：單一 Symbol 技術指標快照 — 純走 <see cref="IMarketContextBuilder"/>、不觸發 LLM。
///         AiAdvisorPanel 用此產 LLM 分析 prompt，使用者複製到外部 LLM。</item>
///   <item><c>GET /api/ai/market-sweep</c>：Top 10 主流幣種技術面快照 + copy-paste ready Prompt 字串。</item>
/// </list>
/// </summary>
public static class AiAdvisorEndpoints
{
    public static IEndpointRouteBuilder MapAiAdvisorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ai").WithTags("AiAdvisor");

        // S74-C 保留：揭露當前 advisor provider 配置（向下兼容；無正式使用者，未來可移）。
        group.MapGet("/config", (IConfiguration config) =>
        {
            var provider = config["AiAdvisor:Provider"] ?? "Sidekick";
            return Results.Ok(new AiAdvisorConfigDto(Provider: provider));
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
        // 組成一份 copy-paste 就能丟到任何 LLM 的分析 Prompt；不呼叫自家 AI（user 自行外部分析）。
        // Interval 預設 OneHour，可被 UI 覆蓋。
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
                sb.Append("- ").Append(s.Symbol).Append("：資料取得失敗(")
                  .Append(s.Error).AppendLine(")");
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

/// <summary>S74-C：<c>GET /api/ai/config</c> — 暴露當前 advisor provider 名稱。S74-D 後預設 "Sidekick"。</summary>
public sealed record AiAdvisorConfigDto(string Provider);

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
