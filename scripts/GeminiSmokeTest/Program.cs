// S30-PRO 自主驗證腳本（Gemini Smoke Test）
// 用途：在 UI 手動操作之外，先以程式直接驗證 Google AI REST 端點可用性。
// 流程：
//   1. 從 cryptobot.db 的 AiCredentials 表讀 Gemini 金鑰（fallback 到 GEMINI_API_KEY 環境變數）。
//   2. 依候選清單（最多 5 次）逐一嘗試 generateContent — 首次 HTTP 2xx + 含文字回應即成功。
//   3. 全數失敗 → 將每一次的 HTTP 狀態、headers、body 寫入 ai_ops/diagnostics/GEMINI_ERROR.log。
//
// 執行方式：
//   從 CryptoBot 專案根目錄：dotnet run --project scripts/GeminiSmokeTest
//   或指定金鑰：GEMINI_API_KEY=... dotnet run --project scripts/GeminiSmokeTest
//
// Exit codes：0 = 成功、2 = 5 次全敗、3 = 金鑰缺失或 DB 不可讀。

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CryptoBot.Scripts.GeminiSmokeTest;

internal sealed record Attempt(string BaseUrl, string Model)
{
    public string Describe() => $"{BaseUrl.TrimEnd('/')}/{Model}:generateContent";
}

internal sealed record AttemptRecord(
    Attempt Attempt,
    int? HttpStatus,
    string? Reason,
    IReadOnlyList<string> Headers,
    string Body);

internal static class Program
{
    // 候選清單 — S30-PRO 要求「允許嘗試不同模型組合」。主目標 2.5-pro，其餘作為降級驗證。
    private static readonly Attempt[] Candidates =
    {
        new("https://generativelanguage.googleapis.com/v1/models/", "gemini-2.5-pro"),
        new("https://generativelanguage.googleapis.com/v1/models/", "gemini-2.5-flash"),
        new("https://generativelanguage.googleapis.com/v1/models/", "gemini-1.5-pro"),
        new("https://generativelanguage.googleapis.com/v1/models/", "gemini-1.5-flash"),
        new("https://generativelanguage.googleapis.com/v1beta/models/", "gemini-1.5-flash"),
    };

    private const int MaxFailuresBeforeAbort = 5;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== Gemini Smoke Test (S30-PRO) ===");

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("✗ 無法取得 Gemini 金鑰：cryptobot.db 查無 AiCredentials[Provider='Gemini']，且未設定 GEMINI_API_KEY 環境變數。");
            Console.Error.WriteLine("  → 請先在 /settings/exchanges 儲存金鑰，或 export GEMINI_API_KEY=... 再重跑。");
            return 3;
        }
        Console.WriteLine($"✓ 金鑰已載入（長度 {apiKey.Length}，preview：{Preview(apiKey)}）");

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        var records = new List<AttemptRecord>();
        Attempt? winner = null;
        string? winnerText = null;

        for (var i = 0; i < Candidates.Length && records.Count < MaxFailuresBeforeAbort; i++)
        {
            var candidate = Candidates[i];
            Console.WriteLine($"[{i + 1}/{Candidates.Length}] 嘗試 {candidate.Describe()} …");

            var record = await TryAsync(http, candidate, apiKey);
            if (record is null)
            {
                // 成功（void-sentinel） — 已列印成功訊息
                winner = candidate;
                // 成功時再取一次以保留訊息
                var (ok, msg) = await ProbeSuccessAsync(http, candidate, apiKey);
                if (ok) winnerText = msg;
                break;
            }

            records.Add(record);
            Console.WriteLine($"   ✗ 失敗：HTTP {record.HttpStatus?.ToString() ?? "—"} {record.Reason}");
        }

        if (winner is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"=== ✓ 自主驗證成功 ===");
            Console.WriteLine($"Model      : {winner.Model}");
            Console.WriteLine($"Endpoint   : {winner.BaseUrl}");
            if (!string.IsNullOrWhiteSpace(winnerText))
            {
                Console.WriteLine($"AI 回應預覽 : {Truncate(winnerText!, 200)}");
            }
            return 0;
        }

        // 全敗 — 寫入診斷 log
        Console.WriteLine();
        Console.WriteLine($"=== ✗ 已連續失敗 {records.Count} 次，觸發熔斷 ===");
        var logPath = await WriteErrorLogAsync(records, apiKey);
        Console.Error.WriteLine($"→ 詳細 HTTP Headers/Body 已存至：{logPath}");
        return 2;
    }

    /// <summary>
    /// 嘗試一次呼叫。成功 → return null；失敗 → return AttemptRecord（含 headers + body）。
    /// </summary>
    private static async Task<AttemptRecord?> TryAsync(HttpClient http, Attempt attempt, string apiKey)
    {
        var url = $"{attempt.BaseUrl.TrimEnd('/')}/{attempt.Model}:generateContent?key={Uri.EscapeDataString(apiKey)}";

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = "回覆「OK」兩個英文字母，不要任何其他內容。" } }
                }
            },
            generationConfig = new
            {
                temperature = 0.1,
                maxOutputTokens = 16
            }
        };

        HttpResponseMessage? resp = null;
        try
        {
            resp = await http.PostAsJsonAsync(url, payload);
            var body = await resp.Content.ReadAsStringAsync();
            var headers = DumpHeaders(resp);

            if (!resp.IsSuccessStatusCode)
            {
                return new AttemptRecord(attempt, (int)resp.StatusCode, resp.ReasonPhrase, headers, body);
            }

            // 成功：檢查內容確實有文字 — 避免回 200 但內容空
            var text = ExtractText(body);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new AttemptRecord(attempt, (int)resp.StatusCode, "回應 JSON 不含 candidates[0].content.parts[0].text", headers, body);
            }
            return null; // success
        }
        catch (TaskCanceledException tex)
        {
            return new AttemptRecord(attempt, null, $"Timeout / Canceled: {tex.Message}", Array.Empty<string>(), string.Empty);
        }
        catch (HttpRequestException hex)
        {
            return new AttemptRecord(attempt, (int?)hex.StatusCode, $"HttpRequestException: {hex.Message}", Array.Empty<string>(), string.Empty);
        }
        catch (Exception ex)
        {
            return new AttemptRecord(attempt, null, $"{ex.GetType().Name}: {ex.Message}", Array.Empty<string>(), string.Empty);
        }
        finally
        {
            resp?.Dispose();
        }
    }

    /// <summary>成功時再探一次以取回 AI 文字 preview。獨立於主流程失敗記錄。</summary>
    private static async Task<(bool ok, string? text)> ProbeSuccessAsync(HttpClient http, Attempt attempt, string apiKey)
    {
        try
        {
            var url = $"{attempt.BaseUrl.TrimEnd('/')}/{attempt.Model}:generateContent?key={Uri.EscapeDataString(apiKey)}";
            var payload = new
            {
                contents = new[] { new { parts = new[] { new { text = "回覆「OK」。" } } } },
                generationConfig = new { temperature = 0.1, maxOutputTokens = 16 }
            };
            using var resp = await http.PostAsJsonAsync(url, payload);
            if (!resp.IsSuccessStatusCode) return (false, null);
            var body = await resp.Content.ReadAsStringAsync();
            return (true, ExtractText(body));
        }
        catch
        {
            return (false, null);
        }
    }

    private static string? ExtractText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates)) return null;
            if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0) return null;
            var first = candidates[0];
            if (!first.TryGetProperty("content", out var content)) return null;
            if (!content.TryGetProperty("parts", out var parts)) return null;
            if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0) return null;
            if (!parts[0].TryGetProperty("text", out var textProp)) return null;
            return textProp.GetString();
        }
        catch { return null; }
    }

    private static IReadOnlyList<string> DumpHeaders(HttpResponseMessage resp)
    {
        var lines = new List<string>();
        foreach (var h in resp.Headers) lines.Add($"{h.Key}: {string.Join(", ", h.Value)}");
        foreach (var h in resp.Content.Headers) lines.Add($"{h.Key}: {string.Join(", ", h.Value)}");
        return lines;
    }

    private static string Preview(string key)
    {
        if (string.IsNullOrEmpty(key)) return "(empty)";
        if (key.Length <= 8) return "••••••";
        return $"{key[..4]}…{key[^4..]}";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string? ResolveApiKey()
    {
        // 1. 嘗試從 cryptobot.db 讀取
        var dbPath = Path.GetFullPath("cryptobot.db");
        if (File.Exists(dbPath))
        {
            try
            {
                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly
                };
                using var conn = new SqliteConnection(csb.ToString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ApiKey FROM AiCredentials WHERE Provider = 'Gemini' LIMIT 1;";
                var result = cmd.ExecuteScalar() as string;
                if (!string.IsNullOrWhiteSpace(result))
                {
                    Console.WriteLine($"  (金鑰來源：{dbPath})");
                    return result.Trim();
                }
                Console.WriteLine($"  (cryptobot.db 存在但 AiCredentials 無 Gemini 列)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  (讀取 cryptobot.db 失敗：{ex.Message})");
            }
        }
        else
        {
            Console.WriteLine($"  (找不到 cryptobot.db：{dbPath})");
        }

        // 2. Fallback 環境變數
        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            Console.WriteLine("  (金鑰來源：環境變數 GEMINI_API_KEY)");
            return envKey.Trim();
        }

        return null;
    }

    private static async Task<string> WriteErrorLogAsync(IReadOnlyList<AttemptRecord> records, string apiKey)
    {
        // 目的路徑：相對於 CryptoBot 專案根目錄的 ai_ops/diagnostics/
        var logDir = Path.GetFullPath("ai_ops/diagnostics");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "GEMINI_ERROR.log");

        var sb = new StringBuilder();
        sb.AppendLine("================================================================");
        sb.AppendLine($"Gemini Smoke Test FAILURE Report");
        sb.AppendLine($"Timestamp : {DateTime.UtcNow:O}");
        sb.AppendLine($"Attempts  : {records.Count}");
        sb.AppendLine($"Key preview: {Preview(apiKey)}");
        sb.AppendLine("================================================================");
        sb.AppendLine();

        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            sb.AppendLine($"--- [{i + 1}] {r.Attempt.Describe()}");
            sb.AppendLine($"    HTTP Status : {r.HttpStatus?.ToString() ?? "(network / timeout)"}");
            sb.AppendLine($"    Reason      : {r.Reason}");
            sb.AppendLine($"    -- Response Headers --");
            foreach (var h in r.Headers) sb.AppendLine($"    {h}");
            sb.AppendLine($"    -- Response Body ({r.Body.Length} bytes) --");
            sb.AppendLine(r.Body);
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(logPath, sb.ToString(), Encoding.UTF8);
        return logPath;
    }
}
