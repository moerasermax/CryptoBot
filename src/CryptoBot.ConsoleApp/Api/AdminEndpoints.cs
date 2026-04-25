using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CryptoBot.ConsoleApp.Api.Dtos;
using CryptoBot.ConsoleApp.Services;
using CryptoBot.Domain.Repositories;
using CryptoBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CryptoBot.ConsoleApp.Api;

/// <summary>
/// <c>/api/admin/*</c> 系統管理 Minimal API 群組（S57）。
///
/// 安全邊界：本路由群組仍然走 <c>IpWhitelistMiddleware</c> 的白名單保護 — Middleware
/// 在 <c>Program.cs</c> 裡掛在最前面，任何來源 IP 沒通過就連這裡的 route 都進不來。
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Admin");

        // ─────────────────── T1：IP 白名單 ───────────────────

        // 回傳「目前這個請求的真實 IP」— 由 UseForwardedHeaders 改寫過的 RemoteIpAddress。
        // UI 據此顯示「你的 IP 是 xxx，點這裡加白名單」的按鈕。
        group.MapGet("/caller-ip", (HttpContext http, IIpWhitelistService svc) =>
        {
            var ip = http.Connection.RemoteIpAddress;
            string? ipStr = ip is null
                ? null
                : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
            if (string.IsNullOrEmpty(ipStr))
                return Results.Ok(new CallerIpDto(Ip: "(unknown)", InWhitelist: false));

            var inList = svc.GetAllowed().Any(x =>
                string.Equals(x, ipStr, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(new CallerIpDto(Ip: ipStr, InWhitelist: inList));
        });

        group.MapGet("/whitelist", (IIpWhitelistService svc) =>
            Results.Ok(new WhitelistDto(svc.GetAllowed())));

        group.MapPost("/whitelist", async (
            AddWhitelistRequest body,
            IIpWhitelistService svc,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Ip))
                return Results.BadRequest(new { error = "Ip is required." });

            var outcome = await svc.AddAsync(body.Ip, ct).ConfigureAwait(false);
            var total = svc.GetAllowed().Count;
            return outcome switch
            {
                WhitelistMutationResult.Added =>
                    Results.Ok(new WhitelistMutationResponse("Added", total)),
                WhitelistMutationResult.AlreadyExists =>
                    Results.Ok(new WhitelistMutationResponse("AlreadyExists", total)),
                WhitelistMutationResult.InvalidFormat =>
                    Results.BadRequest(new WhitelistMutationResponse("InvalidFormat", total)),
                _ => Results.StatusCode(500),
            };
        });

        group.MapDelete("/whitelist/{ip}", async (
            string ip,
            IIpWhitelistService svc,
            CancellationToken ct) =>
        {
            var outcome = await svc.RemoveAsync(ip, ct).ConfigureAwait(false);
            var total = svc.GetAllowed().Count;
            return outcome switch
            {
                WhitelistMutationResult.Removed =>
                    Results.Ok(new WhitelistMutationResponse("Removed", total)),
                WhitelistMutationResult.NotFound =>
                    Results.NotFound(new WhitelistMutationResponse("NotFound", total)),
                _ => Results.StatusCode(500),
            };
        });

        // ─────────────────── T2：深度交易回溯 ───────────────────

        group.MapGet("/positions-deep", async (
            int? limit,
            IPositionRepository positions,
            CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            var rows = await positions.GetRecentClosedAsync(take, ct).ConfigureAwait(false);

            var dtos = rows.Select(p => new DeepPositionDto(
                Id: p.Id,
                Symbol: p.Symbol.BingXFormat,
                Side: p.Side.ToString(),
                Quantity: p.Quantity.Value,
                EntryPrice: p.EntryPrice.Value,
                ExitPrice: p.ExitPrice?.Value,
                RealizedPnL: p.RealizedPnL,
                OpenedAtUtc: p.OpenedAt,
                ClosedAtUtc: p.ClosedAt,
                StrategyType: p.StrategyType,
                Snapshot: ParseSnapshot(p.ParametersSnapshot))).ToList();

            return Results.Ok(dtos);
        });

        // ─────────────────── T3：DB Health / VACUUM ───────────────────

        group.MapGet("/db-health", async (
            AppDbContext db,
            IHostEnvironment env,
            CancellationToken ct) =>
        {
            var source = ResolveDataSource(db, env);
            var (main, total) = MeasureDbBytes(source);

            var positionCount = await db.Positions.CountAsync(ct).ConfigureAwait(false);
            var orderCount    = await db.Orders.CountAsync(ct).ConfigureAwait(false);
            var strategyCount = await db.Strategies.CountAsync(ct).ConfigureAwait(false);

            var (logCount, logBytes) = MeasureLogBytes(env.ContentRootPath);

            return Results.Ok(new DbHealthDto(
                DataSourcePath: source,
                MainFileBytes:  main,
                TotalBytes:     total,
                PositionCount:  positionCount,
                OrderCount:     orderCount,
                StrategyCount:  strategyCount,
                LogFileCount:   logCount,
                LogTotalBytes:  logBytes));
        });

        group.MapPost("/db-vacuum", async (
            AppDbContext db,
            IHostEnvironment env,
            CancellationToken ct) =>
        {
            var source = ResolveDataSource(db, env);
            var before = MeasureDbBytes(source).Total;

            var sw = Stopwatch.StartNew();
            // VACUUM 需要獨占連線。ExecuteSqlRawAsync 會用 EF 的 connection，SQLite 在
            // WAL 模式下 VACUUM 也能工作（Microsoft.Data.Sqlite 會自動 checkpoint）。
            await db.Database.ExecuteSqlRawAsync("VACUUM;", ct).ConfigureAwait(false);
            sw.Stop();

            var after = MeasureDbBytes(source).Total;
            return Results.Ok(new DbVacuumResponse(
                BytesBefore:    before,
                BytesAfter:     after,
                BytesReclaimed: Math.Max(0, before - after),
                ElapsedMs:      (int)sw.ElapsedMilliseconds));
        });

        return app;
    }

    // ─────────────────── helpers ───────────────────

    /// <summary>
    /// T2：把 <c>Position.ParametersSnapshot</c>（StrategyExecutor 序列化的 JSON payload）
    /// 解析成「Leverage=3x / Risk=2% / Parameters.FastSmaPeriod=10 …」的 kv 清單。
    /// 失敗時回空清單 — 老資料格式不一致不該炸整個 API。
    /// </summary>
    private static IReadOnlyList<SnapshotKvDto> ParseSnapshot(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
            return Array.Empty<SnapshotKvDto>();

        var list = new List<SnapshotKvDto>();
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            var root = doc.RootElement;

            // 頂層標量欄位（StrategyExecutor.BuildParametersSnapshot 產出的順序）
            TryAddScalar(root, "leverage",            "Leverage",        v => $"{FormatDecimal(v)}x",   list);
            TryAddScalar(root, "riskPerTradePercent", "Risk/Trade",      v => FormatPercent(v),          list);
            TryAddScalar(root, "stopLossPercent",     "Stop Loss",       v => FormatPercent(v),          list);
            TryAddScalar(root, "takeProfitPercent",   "Take Profit",     v => FormatPercent(v),          list);
            TryAddScalar(root, "trailingStopPercent", "Trailing Stop",   v => FormatPercent(v),          list);

            // 策略自訂參數：parameters 是 Dictionary<string, decimal>
            if (root.TryGetProperty("parameters", out var paramsElem) &&
                paramsElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in paramsElem.EnumerateObject())
                {
                    list.Add(new SnapshotKvDto(
                        Key:   kv.Name,
                        Value: FormatDecimal(kv.Value)));
                }
            }
        }
        catch (JsonException)
        {
            // 老 row 可能是空字串或非 JSON — 靜默回空清單，UI 會顯示「—」。
        }
        return list;
    }

    private static void TryAddScalar(
        JsonElement root, string field, string displayKey,
        Func<JsonElement, string> format, List<SnapshotKvDto> list)
    {
        if (!root.TryGetProperty(field, out var elem)) return;
        if (elem.ValueKind == JsonValueKind.Null) return;
        list.Add(new SnapshotKvDto(displayKey, format(elem)));
    }

    private static string FormatDecimal(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Null) return "—";
        if (v.ValueKind == JsonValueKind.Number)
        {
            // decimal 精度優先，整數不印小數
            if (v.TryGetDecimal(out var d))
            {
                return d == Math.Floor(d)
                    ? ((long)d).ToString(CultureInfo.InvariantCulture)
                    : d.ToString("0.##########", CultureInfo.InvariantCulture);
            }
        }
        return v.ToString();
    }

    private static string FormatPercent(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Null) return "—";
        if (v.TryGetDecimal(out var d))
            return (d * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
        return v.ToString();
    }

    /// <summary>
    /// T3：從 EF 的 connection 取出 SQLite 的 Data Source 路徑。
    /// 相對路徑轉絕對（以 ContentRootPath 為基準）— 否則 UI 顯示 "cryptobot.db" 不直觀。
    /// </summary>
    private static string ResolveDataSource(AppDbContext db, IHostEnvironment env)
    {
        var raw = db.Database.GetDbConnection().DataSource;
        if (string.IsNullOrWhiteSpace(raw)) return "(unknown)";
        return Path.IsPathRooted(raw)
            ? raw
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, raw));
    }

    /// <summary>
    /// T3：量 SQLite 檔案大小。SQLite 在 WAL 模式下會有 -wal / -shm 伴隨檔，
    /// TotalBytes 涵蓋全部，讓使用者看見真正的磁碟佔用；MainBytes 只算主 .db。
    /// </summary>
    private static (long Main, long Total) MeasureDbBytes(string path)
    {
        long main = 0, total = 0;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists) { main = fi.Length; total += fi.Length; }

            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                var aux = new FileInfo(path + suffix);
                if (aux.Exists) total += aux.Length;
            }
        }
        catch { /* 權限或路徑異常時保持 0 — UI 自己判斷 */ }
        return (main, total);
    }

    /// <summary>
    /// T3：量 logs/ 目錄裡的 log 檔案總數 + 總位元組。
    /// Serilog 的 rolling file 規則：<c>logs/cryptobot-*.log</c>，每日一份、保留 14 天。
    /// </summary>
    private static (int Count, long Bytes) MeasureLogBytes(string contentRoot)
    {
        try
        {
            var dir = Path.Combine(contentRoot, "logs");
            if (!Directory.Exists(dir)) return (0, 0);
            var files = new DirectoryInfo(dir).GetFiles("*.log");
            return (files.Length, files.Sum(f => f.Length));
        }
        catch
        {
            return (0, 0);
        }
    }
}
