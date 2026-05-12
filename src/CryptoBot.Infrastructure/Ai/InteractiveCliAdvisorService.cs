using System.Diagnostics;
using System.Globalization;
using System.Text;
using CryptoBot.Application.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoBot.Infrastructure.Ai;

/// <summary>
/// S74-C：互動式本地 CLI Advisor — <b>headless 單發</b> 模式（取代 S74 彈窗 / S74-B Process 持久 stdin）。
///
/// 走 <c>gemini -p &quot;&lt;prompt&gt;&quot;</c> 一次性呼叫、stdout 直接讀；不再管 Process 互動 / TTY / stdin redirect。
/// 這條路只用於 <c>POST /api/ai/advise</c> headless 場景（自動化測試 / 診斷工具 / API client）；
/// UI 的對話 UX 走全域 <c>GlobalAiSidebar</c>，與本服務獨立。
///
/// 永不拋；任何失敗轉 Success=false。
/// </summary>
public sealed class InteractiveCliAdvisorService : IAiAdvisorService
{
    private const string ModelTag = "interactive-cli";

    private readonly InteractiveCliAdvisorOptions _opts;
    private readonly ILogger<InteractiveCliAdvisorService> _logger;

    public InteractiveCliAdvisorService(
        IOptions<InteractiveCliAdvisorOptions> opts,
        ILogger<InteractiveCliAdvisorService> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    public async Task<AiAdviceResult> GetAdviceAsync(AiAdviceRequest request, CancellationToken ct = default)
    {
        try
        {
            var prompt = BuildPrompt(request);

            // Windows cmd 命令列上限 8191 字元；prompt 已壓在 ~1KB，但安全起見偵測一下、超界即退（避免靜默截斷）。
            // 改善路徑：未來支援 stdin pipe 或 temp file，此處先保守失敗。
            if (prompt.Length > 7000)
            {
                return Failure($"Prompt 長度 {prompt.Length} 超過 cmd 安全上限；請使用 GlobalAiSidebar 或減少策略 expected keys 數。");
            }

            var psi = new ProcessStartInfo
            {
                FileName = _opts.Executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(prompt);

            using var process = new Process { StartInfo = psi };
            var stdoutSb = new StringBuilder();
            var stderrSb = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutSb.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrSb.AppendLine(e.Data); };

            if (!process.Start())
            {
                return Failure($"Process.Start 回 false（FileName={_opts.Executable}）。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return Failure($"InteractiveCli 逾時（{_opts.TimeoutSeconds}s）— gemini -p 未在限時內回傳。");
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return Failure("InteractiveCli 請求已取消。");
            }

            if (process.ExitCode != 0)
            {
                var stderr = stderrSb.ToString().Trim();
                return Failure($"gemini -p 退出碼 {process.ExitCode}。stderr：{(string.IsNullOrEmpty(stderr) ? "(空)" : stderr)}");
            }

            var raw = stdoutSb.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Failure("gemini -p 結束但 stdout 為空。");
            }

            if (!AiAdvicePayloadParser.TryParse(raw, request.ExpectedParameterKeys,
                out var commentary, out var parameters, out var error))
            {
                return Failure($"InteractiveCli 解析失敗：{error}");
            }

            return new AiAdviceResult(
                Success: true,
                Commentary: commentary,
                SuggestedParameters: parameters,
                Error: null,
                Model: ModelTag,
                Attempts: Array.Empty<AiAttemptDiagnostic>());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[InteractiveCli] GetAdviceAsync 失敗。");
            return Failure($"InteractiveCli 異常：{ex.Message}");
        }
    }

    public Task<AiModelListResult> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult(new AiModelListResult(
            Success: false,
            Models: Array.Empty<AiModelInfo>(),
            Error: "InteractiveCli 模式不支援遠端模型探測。"));

    private static string BuildPrompt(AiAdviceRequest req)
    {
        var ctx = req.Context;
        var allowedKeys = string.Join(", ", req.ExpectedParameterKeys);
        var firstKey = req.ExpectedParameterKeys.Count > 0 ? req.ExpectedParameterKeys[0] : "ParameterKey";

        return $@"你是一位頂尖量化交易顧問。請依以下市場快照產出 JSON 參數建議。回應**只能**是純 JSON，不要 markdown、不要解釋。

## 市場快照
- 商品：{ctx.Symbol} @ {ctx.Interval}
- RSI(14)：{ctx.Rsi14:F2}
- ATR(14)：{ctx.Atr14:F4}
- 最新價格：{ctx.LatestClose:F2}
- 策略：{req.StrategyDisplayName}（key={req.StrategyKey}）

## 規則
- 只能用以下鍵名（區分大小寫、原字串照抄）：{allowedKeys}
- 每個參數為三元組 {{ ""min"", ""max"", ""step"" }}（小寫、step > 0；無須掃描可 min=max、step=1）

## JSON 格式
{{
  ""commentary"": ""首句寫行情定性（Trending/Ranging），後接設計理由"",
  ""parameters"": {{
    ""{firstKey}"": {{ ""min"": <decimal>, ""max"": <decimal>, ""step"": <decimal> }}
  }}
}}";
    }

    private static AiAdviceResult Failure(string error) =>
        new(false, string.Empty, new Dictionary<string, ParameterGridRange>(), error, ModelTag, Array.Empty<AiAttemptDiagnostic>());
}
