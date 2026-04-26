// S30-LITE T3 自主驗證腳本（Gemini Resilience Test）
// 用途：壓測 GeminiAiAdvisorService 的指數退避機制。連續呼叫 3 輪 generateContent，
//       每輪內部自行實作 up to 5 次 retry（2s→4s→8s→16s→32s）觀察退避效果。
// 模型：gemini-2.5-flash-lite（與 S30-LITE Options 一致）
//
// 退出碼：
//   0 → 3 輪全數成功
//   2 → 某輪 5 次重試仍失敗，詳細 JSON 已寫入 ai_ops/diagnostics/GEMINI_429_DETAIL.log
//   3 → 金鑰缺失（DB 無 AiCredentials / 無 GEMINI_API_KEY 環境變數）

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CryptoBot.Scripts.GeminiResilienceTest;

internal static class Program
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1/models/";
    private const string Model = "gemini-2.5-flash-lite";
    private const int Rounds = 3;
    private const int MaxRetriesPerRound = 5;

    // 2s → 4s → 8s → 16s → 32s（最多 62s 純退避 + 5 次 HTTP 呼叫本身）
    private static readonly int[] BackoffMs = { 2000, 4000, 8000, 16000, 32000 };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"=== Gemini Resilience Test (S30-LITE · {Model}) ===");
        Console.WriteLine($"Rounds: {Rounds}   Max retries/round: {MaxRetriesPerRound}");
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

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        for (var round = 1; round <= Rounds; round++)
        {
            Console.WriteLine($"── Round {round}/{Rounds} ──");
            var outcome = await RunRoundAsync(http, apiKey, round);
            if (outcome.Kind == RoundResultKind.Success)
            {
                Console.WriteLine($"   ✓ Round {round} OK（耗時 {outcome.ElapsedMs}ms，重試 {outcome.Retries} 次）");
                Console.WriteLine($"   AI 回應預覽：{Truncate(outcome.Text ?? "", 120)}");
                Console.WriteLine();
                continue;
            }

            Console.WriteLine($"   ✗ Round {round} 失敗（{outcome.Kind}）");
            var logPath = await WriteDetailLogAsync(round, outcome);
            Console.Error.WriteLine();
            Console.Error.WriteLine($"=== ✗ 自主驗證失敗（Round {round} / {outcome.Kind}）===");
            Console.Error.WriteLine($"詳細 HTTP Response JSON 已存至：{logPath}");
            return 2;
        }

        Console.WriteLine($"=== ✓ {Rounds} 輪全數成功 — 退避機制驗證通過 ===");
        Console.WriteLine($"Model: {Model}");
        return 0;
    }

    private enum RoundResultKind
    {
        Success,
        RetryBudgetExhausted,   // 5 次都被 429/503 打回
        FatalStatus,             // 非暫時性 HTTP 錯誤（400/404 等）
        Exception,               // 網路/timeout
    }

    private sealed record RoundResult(
        RoundResultKind Kind,
        long ElapsedMs,
        int Retries,
        string? Text,
        int? LastHttpStatus,
        IReadOnlyList<string> LastHeaders,
        string LastBody,
        string? Reason);

    private static async Task<RoundResult> RunRoundAsync(HttpClient http, string apiKey, int round)
    {
        var url = $"{BaseUrl.TrimEnd('/')}/{Model}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = $"回覆單一詞：'round-{round}-ok'。" } }
                }
            },
            generationConfig = new { temperature = 0.1, maxOutputTokens = 16 }
        };

        var sw = Stopwatch.StartNew();
        int retries = 0;
        int? lastStatus = null;
        IReadOnlyList<string> lastHeaders = Array.Empty<string>();
        string lastBody = string.Empty;
        string? lastReason = null;

        for (var attempt = 0; attempt <= MaxRetriesPerRound; attempt++)
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
                    return new RoundResult(
                        RoundResultKind.Success,
                        sw.ElapsedMilliseconds, retries, text,
                        lastStatus, lastHeaders, lastBody, null);
                }

                var transient = lastStatus is 429 or 503;
                if (!transient)
                {
                    sw.Stop();
                    lastReason = $"非暫時性 HTTP {lastStatus}";
                    return new RoundResult(
                        RoundResultKind.FatalStatus,
                        sw.ElapsedMilliseconds, retries, null,
                        lastStatus, lastHeaders, lastBody, lastReason);
                }

                if (attempt >= MaxRetriesPerRound)
                {
                    sw.Stop();
                    lastReason = $"HTTP {lastStatus} 重試 {MaxRetriesPerRound} 次仍失敗";
                    return new RoundResult(
                        RoundResultKind.RetryBudgetExhausted,
                        sw.ElapsedMilliseconds, retries, null,
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
                return new RoundResult(
                    RoundResultKind.Exception,
                    sw.ElapsedMilliseconds, retries, null,
                    lastStatus, lastHeaders, lastBody, lastReason);
            }
        }

        sw.Stop();
        return new RoundResult(
            RoundResultKind.RetryBudgetExhausted,
            sw.ElapsedMilliseconds, retries, null,
            lastStatus, lastHeaders, lastBody, lastReason);
    }

    private static async Task<string> WriteDetailLogAsync(int round, RoundResult r)
    {
        var logDir = Path.GetFullPath("ai_ops/diagnostics");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "GEMINI_429_DETAIL.log");

        var sb = new StringBuilder();
        sb.AppendLine("================================================================");
        sb.AppendLine($"Gemini Resilience Test — FAILURE Detail");
        sb.AppendLine($"Timestamp : {DateTime.UtcNow:O}");
        sb.AppendLine($"Model     : {Model}");
        sb.AppendLine($"Round     : {round}");
        sb.AppendLine($"Outcome   : {r.Kind}");
        sb.AppendLine($"Retries   : {r.Retries}");
        sb.AppendLine($"Elapsed   : {r.ElapsedMs}ms");
        sb.AppendLine($"Reason    : {r.Reason}");
        sb.AppendLine("================================================================");
        sb.AppendLine();
        sb.AppendLine($"-- Last HTTP Status --");
        sb.AppendLine($"{r.LastHttpStatus}");
        sb.AppendLine();
        sb.AppendLine($"-- Last Response Headers --");
        foreach (var h in r.LastHeaders) sb.AppendLine(h);
        sb.AppendLine();
        sb.AppendLine($"-- Last Response Body ({r.LastBody.Length} bytes) --");
        sb.AppendLine(r.LastBody);
        sb.AppendLine();

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
