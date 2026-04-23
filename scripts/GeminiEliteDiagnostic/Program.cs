// S30-ELITE T4 自主驗證腳本（Gemini Elite Diagnostic）
// 用途：壓測雙 Pro 備援鏈 — 先試 gemini-3.1-pro-preview，失敗則降級 gemini-2.5-pro。
// 每個模型內部做 up to 5 次 retry（2s/4s/8s/16s/32s 指數退避）。
// 規則：
//   - 任一模型成功 → 印出 Model + 回應 + 耗時，結束碼 0。
//   - 雙 Pro 皆失敗 → 累計失敗次數（Primary 5 + Fallback 5 = 10）
//     若 Pro 模型「總失敗次數 ≥ 5」，將完整 HTTP 封包寫入
//     ai_ops/diagnostics/ELITE_FAILURE.log 供 Gemini（PM）遠端診斷。
//
// 退出碼：
//   0 → 至少一個 Pro 模型成功
//   2 → 雙 Pro 皆失敗，詳細日誌已寫入 ELITE_FAILURE.log
//   3 → 金鑰缺失（DB 無 AiCredentials / 無 GEMINI_API_KEY 環境變數）

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CryptoBot.Scripts.GeminiEliteDiagnostic;

internal static class Program
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1/models/";
    private const string PrimaryModel = "gemini-3.1-pro-preview";
    private const string FallbackModel = "gemini-2.5-pro";
    private const int MaxRetriesPerModel = 5;
    private const int ProFailureThreshold = 5;

    // 2/4/8/16/32 秒 — 比 service 端更激進，用來探索「付費層上限」。
    private static readonly int[] BackoffMs = { 2000, 4000, 8000, 16000, 32000 };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== Gemini Elite Diagnostic (S30-ELITE · 雙 Pro 備援鏈) ===");
        Console.WriteLine($"Primary : {PrimaryModel}");
        Console.WriteLine($"Fallback: {FallbackModel}");
        Console.WriteLine($"Max retries/model: {MaxRetriesPerModel}");
        Console.WriteLine($"Backoff schedule (ms): [{string.Join(", ", BackoffMs)}]");
        Console.WriteLine();

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("✗ 無法取得 Gemini 金鑰：cryptobot.db 查無 AiCredentials[Provider='Gemini']，且未設定 GEMINI_API_KEY 環境變數。");
            return 3;
        }
        Console.WriteLine($"✓ 金鑰已載入（preview：{Preview(apiKey)}）");
        Console.WriteLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var totalFailures = 0;
        var outcomes = new List<ModelOutcome>();

        // ─── Primary ────────────────────────────────────────────────
        Console.WriteLine($"── 嘗試 Primary：{PrimaryModel} ──");
        var primary = await RunModelAsync(http, apiKey, PrimaryModel);
        outcomes.Add(primary);
        if (primary.Kind == OutcomeKind.Success)
        {
            PrintSuccess(primary);
            Console.WriteLine($"=== ✓ Primary {PrimaryModel} 成功 — 雙 Pro 鏈驗證通過 ===");
            return 0;
        }
        totalFailures += primary.Retries + 1;
        Console.WriteLine($"   ✗ Primary 失敗（{primary.Kind}，重試 {primary.Retries} 次，最終 HTTP {primary.LastHttpStatus})");
        Console.WriteLine();

        // ─── 模擬 service 的 3s 停頓 ────────────────────────────────
        Console.WriteLine($"── 模擬 Service 的 3s 停頓後切換 Fallback ──");
        await Task.Delay(3000);
        Console.WriteLine();

        // ─── Fallback ──────────────────────────────────────────────
        Console.WriteLine($"── 嘗試 Fallback：{FallbackModel} ──");
        var fallback = await RunModelAsync(http, apiKey, FallbackModel);
        outcomes.Add(fallback);
        if (fallback.Kind == OutcomeKind.Success)
        {
            PrintSuccess(fallback);
            Console.WriteLine($"=== ✓ Fallback {FallbackModel} 接手成功 — 雙 Pro 鏈驗證通過 ===");
            return 0;
        }
        totalFailures += fallback.Retries + 1;
        Console.WriteLine($"   ✗ Fallback 失敗（{fallback.Kind}，重試 {fallback.Retries} 次，最終 HTTP {fallback.LastHttpStatus})");
        Console.WriteLine();

        // ─── 雙 Pro 皆敗 → 寫 ELITE_FAILURE.log ────────────────────
        Console.Error.WriteLine($"=== ✗ 雙 Pro 皆失敗（總失敗次數 {totalFailures}）===");
        if (totalFailures >= ProFailureThreshold)
        {
            var logPath = await WriteFailureLogAsync(outcomes, totalFailures);
            Console.Error.WriteLine($"失敗次數超過熔斷閾值 {ProFailureThreshold}，完整 HTTP 封包已寫入：{logPath}");
        }
        return 2;
    }

    private enum OutcomeKind { Success, RetryBudgetExhausted, FatalStatus, Exception }

    private sealed record ModelOutcome(
        string Model,
        OutcomeKind Kind,
        long ElapsedMs,
        int Retries,
        string? Text,
        int? LastHttpStatus,
        IReadOnlyList<string> LastHeaders,
        string LastBody,
        string? Reason);

    private static async Task<ModelOutcome> RunModelAsync(HttpClient http, string apiKey, string model)
    {
        var url = $"{BaseUrl.TrimEnd('/')}/{model}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = $"你是量化顧問。用一句中文回覆：你目前是哪個模型？直接回答不要解釋，不超過 20 字。" } }
                }
            },
            generationConfig = new { temperature = 0.2, maxOutputTokens = 32 }
        };

        var sw = Stopwatch.StartNew();
        int retries = 0;
        int? lastStatus = null;
        IReadOnlyList<string> lastHeaders = Array.Empty<string>();
        string lastBody = string.Empty;
        string? lastReason = null;

        for (var attempt = 0; attempt <= MaxRetriesPerModel; attempt++)
        {
            try
            {
                using var resp = await http.PostAsJsonAsync(url, payload);
                lastStatus = (int)resp.StatusCode;
                lastHeaders = DumpHeaders(resp);
                lastBody = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                {
                    var text = ExtractText(lastBody);
                    sw.Stop();
                    return new ModelOutcome(model, OutcomeKind.Success, sw.ElapsedMilliseconds, retries, text,
                        lastStatus, lastHeaders, lastBody, null);
                }

                var transient = lastStatus is 429 or 503;
                if (!transient)
                {
                    sw.Stop();
                    lastReason = $"非暫時性 HTTP {lastStatus}";
                    return new ModelOutcome(model, OutcomeKind.FatalStatus, sw.ElapsedMilliseconds, retries, null,
                        lastStatus, lastHeaders, lastBody, lastReason);
                }

                if (attempt >= MaxRetriesPerModel)
                {
                    sw.Stop();
                    lastReason = $"HTTP {lastStatus} 重試 {MaxRetriesPerModel} 次仍失敗";
                    return new ModelOutcome(model, OutcomeKind.RetryBudgetExhausted, sw.ElapsedMilliseconds, retries, null,
                        lastStatus, lastHeaders, lastBody, lastReason);
                }

                var delay = BackoffMs[attempt];
                retries++;
                Console.WriteLine($"   [AI-RETRY] Attempt {attempt + 1} due to HTTP {lastStatus} — 退避 {delay}ms…");
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                sw.Stop();
                lastReason = $"{ex.GetType().Name}: {ex.Message}";
                return new ModelOutcome(model, OutcomeKind.Exception, sw.ElapsedMilliseconds, retries, null,
                    lastStatus, lastHeaders, lastBody, lastReason);
            }
        }

        sw.Stop();
        return new ModelOutcome(model, OutcomeKind.RetryBudgetExhausted, sw.ElapsedMilliseconds, retries, null,
            lastStatus, lastHeaders, lastBody, lastReason);
    }

    private static void PrintSuccess(ModelOutcome o)
    {
        Console.WriteLine($"   ✓ {o.Model} OK（耗時 {o.ElapsedMs}ms，重試 {o.Retries} 次）");
        Console.WriteLine($"   AI 回應預覽：{Truncate(o.Text ?? "", 120)}");
        Console.WriteLine();
    }

    private static async Task<string> WriteFailureLogAsync(List<ModelOutcome> outcomes, int totalFailures)
    {
        var logDir = Path.GetFullPath("ai_ops/diagnostics");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "ELITE_FAILURE.log");

        var sb = new StringBuilder();
        sb.AppendLine("================================================================");
        sb.AppendLine("Gemini Elite Diagnostic — DUAL-PRO FAILURE Detail");
        sb.AppendLine($"Timestamp      : {DateTime.UtcNow:O}");
        sb.AppendLine($"Total failures : {totalFailures}  (threshold {ProFailureThreshold})");
        sb.AppendLine("================================================================");
        sb.AppendLine();

        foreach (var o in outcomes)
        {
            sb.AppendLine($"========== Model: {o.Model} ==========");
            sb.AppendLine($"Outcome   : {o.Kind}");
            sb.AppendLine($"Retries   : {o.Retries}");
            sb.AppendLine($"Elapsed   : {o.ElapsedMs}ms");
            sb.AppendLine($"Reason    : {o.Reason}");
            sb.AppendLine();
            sb.AppendLine($"-- Last HTTP Status --");
            sb.AppendLine($"{o.LastHttpStatus}");
            sb.AppendLine();
            sb.AppendLine($"-- Last Response Headers --");
            foreach (var h in o.LastHeaders) sb.AppendLine(h);
            sb.AppendLine();
            sb.AppendLine($"-- Last Response Body ({o.LastBody.Length} bytes) --");
            sb.AppendLine(o.LastBody);
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(logPath, sb.ToString(), Encoding.UTF8);
        return logPath;
    }

    private static string? ExtractText(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var candidates = doc.RootElement.GetProperty("candidates");
            if (candidates.GetArrayLength() == 0) return null;
            var parts = candidates[0].GetProperty("content").GetProperty("parts");
            if (parts.GetArrayLength() == 0) return null;
            return parts[0].GetProperty("text").GetString();
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
                if (cmd.ExecuteScalar() is string key && !string.IsNullOrWhiteSpace(key))
                {
                    Console.WriteLine($"  (金鑰來源：{dbPath})");
                    return key.Trim();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  (讀取 cryptobot.db 失敗：{ex.Message})");
            }
        }

        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            Console.WriteLine("  (金鑰來源：環境變數 GEMINI_API_KEY)");
            return envKey.Trim();
        }
        return null;
    }
}
