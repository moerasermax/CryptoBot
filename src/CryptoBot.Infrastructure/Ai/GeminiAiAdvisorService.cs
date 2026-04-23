using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoBot.Application.Ai;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.AiCredentialAggregate;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoBot.Infrastructure.Ai;

public sealed class GeminiAiAdvisorService : IAiAdvisorService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // S30-LITE：每個模型內部的指數退避排程（429/503 專用）。
    private static readonly int[] BackoffMs = { 2000, 4000, 8000 };

    // S30-ELITE：Primary → Fallback 切換時的停頓，讓配額 / overload 稍作緩解。
    private const int InterModelPauseMs = 3000;

    private readonly HttpClient _http;
    private readonly IAiCredentialProvider _credentials;
    private readonly IAiAdviceTraceLog _traceLog;
    private readonly ILogger<GeminiAiAdvisorService> _logger;
    private readonly GeminiOptions _opts;

    public GeminiAiAdvisorService(
        HttpClient http,
        IAiCredentialProvider credentials,
        IAiAdviceTraceLog traceLog,
        IOptions<GeminiOptions> opts,
        ILogger<GeminiAiAdvisorService> logger)
    {
        _http = http;
        _credentials = credentials;
        _traceLog = traceLog;
        _logger = logger;
        _opts = opts.Value;
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(3, _opts.TimeoutSeconds));
    }

    public async Task<AiAdviceResult> GetAdviceAsync(AiAdviceRequest request, CancellationToken ct = default)
    {
        var attempts = new List<AiAttemptDiagnostic>(2);

        var apiKey = await _credentials.GetApiKeyAsync(AiCredential.GeminiProvider, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return RecordAndReturn(Failure("Gemini API Key 尚未配置。", attempts));
        }

        // S30-FIX2：在請求最前頭一次性決定 (Primary, Fallback) 配對。
        // 用 cache 後的 mode 跑完整段流程，避免半路 UI 切模式造成 Primary/Fallback 分屬不同層級。
        var mode = await _credentials.GetModeAsync(AiCredential.GeminiProvider, ct).ConfigureAwait(false);
        var (primaryModel, fallbackModel) = ResolveModelPair(mode);

        var prompt = BuildPrompt(request);

        // 某些 API Key 版本在 v1 端點不支援強制 responseMimeType="application/json" → HTTP 400。
        // 改靠 Prompt + ExtractJson 韌性解析。
        var reqBody = new GeminiRequest(
            Contents: new[] { new GeminiContent(new[] { new GeminiPart(prompt) }) },
            GenerationConfig: new GeminiGenerationConfig(
                Temperature: 0.4,
                MaxOutputTokens: _opts.MaxOutputTokens));

        // S30-ELITE：雙 Pro 自動降級鏈。每次嘗試都回一份 AiAttemptDiagnostic，
        // 供 UI / /api/ai/traces 在失敗時說明清楚「哪個模型在哪一步掛掉」。
        var primaryAttempt = await TryModelAsync(primaryModel, "primary", apiKey, reqBody, request, ct).ConfigureAwait(false);
        attempts.Add(primaryAttempt.Diagnostic);
        if (primaryAttempt.Succeeded)
        {
            return RecordAndReturn(primaryAttempt.Result! with { Attempts = attempts });
        }

        if (ShouldSwitchToFallback(primaryAttempt.Diagnostic.HttpStatus))
        {
            _logger.LogWarning(
                "[AI-ELITE] Mode={Mode} switched to Fallback ({Fallback}) due to error {Status} on {Primary} — 停頓 {Pause}ms 後重試。",
                mode, fallbackModel, primaryAttempt.Diagnostic.HttpStatus, primaryModel, InterModelPauseMs);

            try { await Task.Delay(InterModelPauseMs, ct).ConfigureAwait(false); }
            catch (TaskCanceledException)
            {
                return RecordAndReturn(Failure("Gemini 請求已取消。", attempts));
            }

            var fallbackAttempt = await TryModelAsync(fallbackModel, "fallback", apiKey, reqBody, request, ct).ConfigureAwait(false);
            attempts.Add(fallbackAttempt.Diagnostic);
            if (fallbackAttempt.Succeeded)
            {
                return RecordAndReturn(fallbackAttempt.Result! with { Attempts = attempts });
            }

            _logger.LogWarning(
                "[AI-ELITE] Fallback {Fallback} 也失敗（status={Status}）；mode={Mode} 配對皆耗盡。",
                fallbackModel, fallbackAttempt.Diagnostic.HttpStatus, mode);

            var merged = fallbackAttempt.Result ?? Failure(
                $"Gemini {mode} 配對皆失敗（Primary={primaryAttempt.Diagnostic.HttpStatus}, Fallback={fallbackAttempt.Diagnostic.HttpStatus}）。",
                attempts);
            return RecordAndReturn(merged with { Attempts = attempts });
        }

        var primaryFailure = primaryAttempt.Result
            ?? Failure($"Gemini HTTP {primaryAttempt.Diagnostic.HttpStatus}：請求被拒。", attempts);
        return RecordAndReturn(primaryFailure with { Attempts = attempts });
    }

    /// <summary>
    /// S30-FIX2：把 <see cref="AiAdvisorMode"/> 映射成 (Primary, Fallback) 模型名稱。
    /// 配對由 <see cref="GeminiOptions"/> 提供，預設 Eco=Flash 系、Pro=3.1-pro-preview + 2.5-pro。
    /// 集中此處避免散在 Try/Switch/Log 三處重複 if (mode == ...)。
    /// </summary>
    private (string Primary, string Fallback) ResolveModelPair(AiAdvisorMode mode) => mode switch
    {
        AiAdvisorMode.Pro => (_opts.ProPrimaryModel, _opts.ProFallbackModel),
        _ => (_opts.EcoPrimaryModel, _opts.EcoFallbackModel),
    };

    private AiAdviceResult RecordAndReturn(AiAdviceResult result)
    {
        try { _traceLog.Record(result); }
        catch (Exception ex)
        {
            // Ring buffer 絕不能讓主流程掛掉 — 吃掉任何意外（容量溢出、併發等），只記 log。
            _logger.LogWarning(ex, "[AI-TRACE] 寫入 trace log 失敗（不影響 AI 結果回傳）。");
        }
        return result;
    }

    /// <summary>
    /// 組 Google GenAI REST 的 <c>models</c> collection URL：
    /// <c>{BaseUrl}/{ApiVersion}/models</c>。呼叫端可直接拼 <c>/{model}:generateContent</c>
    /// 或 <c>?key=...</c> 做 ListModels。集中組字串，避免兩處 TryModel/ListModels 各寫一次走歪。
    /// </summary>
    private string BuildModelsCollectionUrl() =>
        $"{_opts.BaseUrl.TrimEnd('/')}/{_opts.ApiVersion.Trim('/')}/models";

    /// <summary>
    /// 對 <paramref name="model"/> 執行一次「HTTP + 429/503 指數退避」序列。
    /// 成功 → 解析 + 回 <see cref="AiAdviceResult"/>；失敗 → 回帶 status 的 attempt 讓上層決定是否切換。
    /// 無論成敗都會帶回一份 <see cref="AiAttemptDiagnostic"/> 供 UI / trace log 使用。
    /// </summary>
    private async Task<ModelTryOutcome> TryModelAsync(
        string model,
        string phase,
        string apiKey,
        GeminiRequest reqBody,
        AiAdviceRequest request,
        CancellationToken ct)
    {
        var url = $"{BuildModelsCollectionUrl()}/{model}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var retryCount = 0;

        HttpResponseMessage? resp = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                resp?.Dispose();
                resp = await _http.PostAsJsonAsync(url, reqBody, JsonOpts, ct).ConfigureAwait(false);
                var transient = (int)resp.StatusCode is 429 or 503;
                if (resp.IsSuccessStatusCode || !transient || attempt >= BackoffMs.Length)
                {
                    break;
                }
                _logger.LogWarning(
                    "[AI-RETRY] Attempt {Attempt} due to HTTP {Status} — 退避 {Delay}ms 後重試（模型 {Model}）",
                    attempt + 1, (int)resp.StatusCode, BackoffMs[attempt], model);
                retryCount++;
                await Task.Delay(BackoffMs[attempt], ct).ConfigureAwait(false);
            }

            if (!resp.IsSuccessStatusCode)
            {
                var errorBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var status = (int)resp.StatusCode;
                var errorMessage = TryExtractErrorMessage(errorBody);
                var failure = BuildFailureForStatus(model, status, errorBody);
                sw.Stop();
                return ModelTryOutcome.Failed(new AiAttemptDiagnostic(
                    Model: model,
                    Phase: phase,
                    HttpStatus: status,
                    FinishReason: null,
                    SafetyBlock: null,
                    ErrorMessage: errorMessage ?? failure.Error,
                    RetryCount: retryCount,
                    DurationMs: (int)sw.ElapsedMilliseconds,
                    At: startedAt),
                    failure);
            }

            var parsed = await resp.Content.ReadFromJsonAsync<GeminiResponse>(JsonOpts, ct).ConfigureAwait(false);
            var candidate = parsed?.Candidates?.FirstOrDefault();
            var finishReason = candidate?.FinishReason;
            var safetyBlock = DescribeSafetyBlock(parsed, candidate);
            var text = candidate?.Content?.Parts?.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                // MAX_TOKENS 是最常見的「假空內容」— 輸出被 token 上限砍斷，JSON 是半截。
                // 明確告訴使用者怎麼救（調高 GeminiOptions.MaxOutputTokens），避免再次通靈。
                var emptyReason = safetyBlock is not null
                    ? $"模型 {model} 沒有回傳內容（安全過濾：{safetyBlock}）。"
                    : finishReason == "MAX_TOKENS"
                        ? $"模型 {model} 輸出被 MaxOutputTokens={_opts.MaxOutputTokens} 截斷 — 請到 appsettings 調高 Gemini.MaxOutputTokens 或設 Gemini__MaxOutputTokens 環境變數。"
                        : finishReason is not null and not "STOP"
                            ? $"模型 {model} 沒有回傳內容（finishReason={finishReason}）。"
                            : $"模型 {model} 沒有回傳內容。";
                sw.Stop();
                return ModelTryOutcome.Failed(new AiAttemptDiagnostic(
                    Model: model,
                    Phase: phase,
                    HttpStatus: 200,
                    FinishReason: finishReason,
                    SafetyBlock: safetyBlock,
                    ErrorMessage: emptyReason,
                    RetryCount: retryCount,
                    DurationMs: (int)sw.ElapsedMilliseconds,
                    At: startedAt),
                    Failure(emptyReason));
            }

            var cleanedJson = ExtractJson(text);
            try
            {
                var payload = JsonSerializer.Deserialize<AiPayload>(cleanedJson, JsonOpts);
                if (payload is null)
                {
                    sw.Stop();
                    var nullMsg = $"模型 {model} JSON 解析失敗 (Null)。";
                    return ModelTryOutcome.Failed(new AiAttemptDiagnostic(
                        Model: model,
                        Phase: phase,
                        HttpStatus: 200,
                        FinishReason: finishReason,
                        SafetyBlock: safetyBlock,
                        ErrorMessage: nullMsg,
                        RetryCount: retryCount,
                        DurationMs: (int)sw.ElapsedMilliseconds,
                        At: startedAt),
                        Failure(nullMsg));
                }

                var sanitized = SanitizeParameters(payload.Parameters, request.ExpectedParameterKeys);

                // S30-FIX2 T2-c：AI 偶爾整批用我們不認得的 key（例如改用 snake_case 或翻譯成中文），
                // 過 SanitizeParameters 後就成了空 dict，UI 顯示成功但「填入」按鈕沒參數可填。
                // 收到非空 raw 但過濾後變空，視為失敗並把 raw key 列出來，幫 user 馬上看出 prompt 沒被遵守。
                if (sanitized.Count == 0 && payload.Parameters is { Count: > 0 } rawParams)
                {
                    sw.Stop();
                    var rawKeys = string.Join(", ", rawParams.Keys);
                    var expected = string.Join(", ", request.ExpectedParameterKeys);
                    var emptyMsg = $"模型 {model} 回傳的參數鍵不在合法清單內。AI 給的鍵：[{rawKeys}]；期待的鍵：[{expected}]。";
                    return ModelTryOutcome.Failed(new AiAttemptDiagnostic(
                        Model: model,
                        Phase: phase,
                        HttpStatus: 200,
                        FinishReason: finishReason,
                        SafetyBlock: safetyBlock,
                        ErrorMessage: emptyMsg,
                        RetryCount: retryCount,
                        DurationMs: (int)sw.ElapsedMilliseconds,
                        At: startedAt),
                        Failure(emptyMsg));
                }

                sw.Stop();
                var ok = new AiAdviceResult(
                    Success: true,
                    Commentary: payload.Commentary ?? string.Empty,
                    SuggestedParameters: sanitized,
                    Error: null,
                    Model: model,
                    Attempts: Array.Empty<AiAttemptDiagnostic>());
                return ModelTryOutcome.Ok(new AiAttemptDiagnostic(
                    Model: model,
                    Phase: phase,
                    HttpStatus: 200,
                    FinishReason: finishReason,
                    SafetyBlock: safetyBlock,
                    ErrorMessage: null,
                    RetryCount: retryCount,
                    DurationMs: (int)sw.ElapsedMilliseconds,
                    At: startedAt),
                    ok);
            }
            catch (JsonException jex)
            {
                _logger.LogWarning(jex, "Gemini JSON 解析失敗（模型 {Model}）。原始文字：{Raw}", model, text);
                sw.Stop();
                var jsonMsg = $"模型 {model} JSON 解析失敗：{jex.Message}";
                return ModelTryOutcome.Failed(new AiAttemptDiagnostic(
                    Model: model,
                    Phase: phase,
                    HttpStatus: 200,
                    FinishReason: finishReason,
                    SafetyBlock: safetyBlock,
                    ErrorMessage: jsonMsg,
                    RetryCount: retryCount,
                    DurationMs: (int)sw.ElapsedMilliseconds,
                    At: startedAt),
                    Failure(jsonMsg));
            }
        }
        catch (Exception ex)
        {
            // 網路/timeout → 當成可切 Fallback 的暫時性錯誤（status=0 代表無 HTTP 回應）。
            _logger.LogWarning(ex, "Gemini 請求異常（模型 {Model}）。", model);
            sw.Stop();
            var exMsg = $"Gemini 請求異常（模型 {model}）：{ex.Message}";
            return ModelTryOutcome.Failed(new AiAttemptDiagnostic(
                Model: model,
                Phase: phase,
                HttpStatus: 0,
                FinishReason: null,
                SafetyBlock: null,
                ErrorMessage: exMsg,
                RetryCount: retryCount,
                DurationMs: (int)sw.ElapsedMilliseconds,
                At: startedAt),
                Failure(exMsg));
        }
        finally
        {
            resp?.Dispose();
        }
    }

    private AiAdviceResult BuildFailureForStatus(string model, int status, string errorBody)
    {
        // 404 → 模型名稱在此 API 版本不存在（模型退役或打錯）
        // 400/401/403 → 金鑰格式錯誤或 payload 被拒
        // 429/503 → Rate limit / overloaded（已走過退避重試仍失敗）
        if (status == 404)
        {
            _logger.LogWarning(
                "Gemini 404：模型 {Model} 在端點 {Endpoint} 找不到 — 可能已退役，或該模型僅存於另一個 API version（例 preview 模型常只在 v1beta）。原始訊息：{Body}",
                model, BuildModelsCollectionUrl(), errorBody);
            return Failure($"Gemini 404：模型 {model} 在 API version {_opts.ApiVersion} 端點找不到。若為 preview 模型，請將 Gemini:ApiVersion 設為 v1beta。");
        }
        if (status == 400 || status == 401 || status == 403)
        {
            _logger.LogWarning(
                "Gemini {Status}：請求被拒 — 請檢查金鑰是否正確 / 模型 {Model} 是否可用。原始訊息：{Body}",
                status, model, errorBody);
            return Failure($"Gemini HTTP {status}：金鑰無效或模型 {model} 不可用。");
        }
        if (status == 429 || status == 503)
        {
            _logger.LogWarning(
                "Gemini {Status}：模型 {Model} 經 {Retries} 次退避重試仍失敗（速率限制 / overloaded）。原始訊息：{Body}",
                status, model, BackoffMs.Length, errorBody);
            return Failure($"Gemini {status}：模型 {model} 已觸發速率限制（經 {BackoffMs.Length} 次退避仍失敗）。");
        }

        _logger.LogWarning("Gemini HTTP {Status}（模型 {Model}），原始訊息：{Body}", status, model, errorBody);
        return Failure($"Gemini HTTP {status}（模型 {model}）：{errorBody}");
    }

    /// <summary>
    /// S30-ELITE：Primary 結束在以下 status 時，才降級到 Fallback——
    ///   429/503：速率限制或 overload，換模型有機會成功
    ///   404：模型退役，換成 FallbackModel 也許仍在
    ///   0：網路/timeout 類無 HTTP status 的例外
    /// 400/401/403/其他：金鑰問題或程式 bug，換模型也救不回，直接失敗讓用戶介入。
    /// </summary>
    private static bool ShouldSwitchToFallback(int status) =>
        status is 0 or 404 or 429 or 503;

    private static string ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (end > 7)
            {
                return trimmed[7..end].Trim();
            }
        }
        else if (trimmed.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (end > 3)
            {
                return trimmed[3..end].Trim();
            }
        }
        return trimmed;
    }

    private static AiAdviceResult Failure(string error) =>
        new(false, string.Empty, new Dictionary<string, ParameterGridRange>(), error, "gemini", Array.Empty<AiAttemptDiagnostic>());

    private static AiAdviceResult Failure(string error, IReadOnlyList<AiAttemptDiagnostic> attempts) =>
        new(false, string.Empty, new Dictionary<string, ParameterGridRange>(), error, "gemini", attempts);

    /// <summary>
    /// 嘗試從 Gemini error JSON（<c>{ "error": { "message": ... } }</c>）抓 message 欄位。
    /// 失敗就回 null，讓上層 fallback 到我們自己組的失敗字串。
    /// </summary>
    private static string? TryExtractErrorMessage(string errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody)) return null;
        try
        {
            var err = JsonSerializer.Deserialize<GeminiErrorResponse>(errorBody, JsonOpts);
            return err?.Error?.Message;
        }
        catch { return null; }
    }

    /// <summary>
    /// 把 Gemini 回應的 safety 相關資訊濃縮成單行診斷訊息。
    /// 三個資訊來源：
    ///   1. <c>promptFeedback.blockReason</c>：輸入被整體擋下（例如 SAFETY / OTHER）
    ///   2. candidate 的 finishReason == SAFETY + <c>safetyRatings[].blocked=true</c>
    ///   3. 都沒有 → 回 null（代表沒有安全過濾干預）
    /// </summary>
    private static string? DescribeSafetyBlock(GeminiResponse? resp, GeminiCandidate? candidate)
    {
        var promptBlock = resp?.PromptFeedback?.BlockReason;
        if (!string.IsNullOrWhiteSpace(promptBlock))
        {
            return $"promptFeedback.blockReason={promptBlock}";
        }

        if (candidate?.FinishReason == "SAFETY" && candidate.SafetyRatings is { Length: > 0 } ratings)
        {
            var blocked = ratings
                .Where(r => r.Blocked == true)
                .Select(r => $"{r.Category}:{r.Probability}")
                .ToArray();
            if (blocked.Length > 0)
            {
                return string.Join(", ", blocked);
            }
            return "SAFETY (未標 blocked 細項，模型自我審查)";
        }

        return null;
    }

    // S30-GRID：AI 可能回傳兩種形態——
    //   (a) 新版網格：{"FastSmaPeriod": {"min": 5, "max": 20, "step": 1}}
    //   (b) 舊版單點：{"FastSmaPeriod": 10}（退化為 min=max=10, step=1）
    // 我們同時支援，並在欄位缺漏時以容錯預設值補足（min/max 互補、step 至少 1）。
    private static IReadOnlyDictionary<string, ParameterGridRange> SanitizeParameters(
        IDictionary<string, JsonElement>? raw, IReadOnlyList<string> expectedKeys)
    {
        var result = new Dictionary<string, ParameterGridRange>();
        if (raw is null) return result;

        var allowed = new HashSet<string>(expectedKeys, StringComparer.OrdinalIgnoreCase);
        var keyMap = expectedKeys.ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var kv in raw)
        {
            if (!allowed.Contains(kv.Key)) continue;
            if (!keyMap.TryGetValue(kv.Key, out var officialKey)) continue;

            if (TryParseGridRange(kv.Value, out var range))
            {
                result[officialKey] = range;
            }
        }
        return result;
    }

    // S30-FIX2 T2-a：AI 偶爾不照規矩交「min/max/step」三件套，會吐 minimum/maximum/increment 之類的同義詞。
    // 與其每次失敗就要 user 重按，不如把同義詞收齊一次解決。順序內維持：先 min 系、再 max 系、再 step 系。
    private static readonly string[] MinAliases = { "min", "Min", "minimum", "Minimum", "from", "From", "start", "Start", "low", "Low" };
    private static readonly string[] MaxAliases = { "max", "Max", "maximum", "Maximum", "to", "To", "end", "End", "high", "High" };
    private static readonly string[] StepAliases = { "step", "Step", "increment", "Increment", "gap", "Gap", "interval", "Interval", "stride", "Stride" };

    private static bool TryParseGridRange(JsonElement value, out ParameterGridRange range)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
            {
                var v = value.GetDecimal();
                range = new ParameterGridRange(v, v, 1m);
                return true;
            }
            case JsonValueKind.Object:
            {
                var min = TryGetDecimalAny(value, MinAliases);
                var max = TryGetDecimalAny(value, MaxAliases);
                var step = TryGetDecimalAny(value, StepAliases);

                if (min is null && max is null) { range = default!; return false; }

                var minVal = min ?? max!.Value;
                var maxVal = max ?? min!.Value;
                if (maxVal < minVal) (minVal, maxVal) = (maxVal, minVal);

                var stepVal = step ?? 1m;
                if (stepVal <= 0m) stepVal = 1m;

                range = new ParameterGridRange(minVal, maxVal, stepVal);
                return true;
            }
            default:
                range = default!;
                return false;
        }
    }

    private static decimal? TryGetDecimalAny(JsonElement obj, string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            var v = TryGetDecimal(obj, name);
            if (v is not null) return v;
        }
        return null;
    }

    private static decimal? TryGetDecimal(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static string BuildPrompt(AiAdviceRequest req)
    {
        // S30-ELITE T2：從「網格建議」進一步升級為「行情定性 + 雙目標優化 + 三維網格」。
        // 同時保留 S30-GRID 的 min/max/step 結構合約。
        // S30-FIX2 T2-b：把合法 key 列出在 example 與 schema 兩處，並明確寫「ParameterKey 是 placeholder」，
        // 避免某些模型 (尤其 flash 系) 看到 ""ParameterKey"" 時直接抄字串當 key。
        var ctx = req.Context;
        var allowedKeys = string.Join(", ", req.ExpectedParameterKeys);
        var firstKey = req.ExpectedParameterKeys.Count > 0 ? req.ExpectedParameterKeys[0] : "ParameterKey";
        return $@"你是一位頂尖量化交易顧問，為回測系統設計「高精度參數優化掃描」。
你必須先判斷當前市場性格，再據此為策略輸出掃描網格。

## 市場快照
- 商品：{ctx.Symbol} @ {ctx.Interval}
- RSI(14)：{ctx.Rsi14:F2}
- ATR(14)：{ctx.Atr14:F4}
- 最新價格：{ctx.LatestClose:F2}

## 步驟 1｜行情定性
判斷當前市場屬於 **Trending（趨勢）** 還是 **Ranging（震盪）**，並在 commentary 首句明確寫出你的分類（例如「目前為 Trending 市場」）。

## 步驟 2｜雙目標優化
為 {req.StrategyDisplayName} 策略設計掃描網格，目標：
1. **Sharpe Ratio 最大化** — 追求每單位風險的穩定報酬。
2. **Max Drawdown 最小化** — 控制最大回撤在可接受範圍。

## 步驟 3｜三維網格輸出
每個參數必須回傳 `{{ ""min"": x, ""max"": y, ""step"": z }}` 三元組：
- Trending 市場：拉大慢線/週期類參數的 min/max，給趨勢有時間累積。
- Ranging 市場：收窄區間、縮小 step，做精細回測找短週期最佳點。
- step 必須 > 0。
- 若某參數極度精確、無須掃描：min = max，step = 1。

## 參數鍵嚴格規則（違反會被系統拒絕）
- **只能使用以下鍵名（區分大小寫，原字串照抄）**：{allowedKeys}
- 不要翻譯成中文、不要改成 snake_case、不要用同義詞、不要新增鍵、不要省略鍵。
- 三元組欄位名固定為小寫：`min` / `max` / `step`（不要寫 minimum / maximum / increment）。
- 範例中的 `{firstKey}` 是上方清單裡的真實鍵；其他鍵也要按照清單原字串使用。

## 回傳格式（純 JSON，不要 Markdown / 不要前後綴）
{{
  ""commentary"": ""首句寫行情定性（Trending/Ranging），後接為何這樣設 min/max/step 與 Sharpe/Drawdown 考量。"",
  ""parameters"": {{
    ""{firstKey}"": {{ ""min"": <decimal>, ""max"": <decimal>, ""step"": <decimal> }}
  }}
}}";
    }

    /// <summary>
    /// 呼 Google ListModels API 探測此金鑰當前可用的模型清單。
    ///
    /// URL 組法：<see cref="BuildModelsCollectionUrl"/> 直接就是 ListModels collection URL，附上
    /// key + pageSize=200（Gemini 的模型總數目前約 50，200 綽綽有餘）。不同 <see cref="GeminiOptions.ApiVersion"/>
    /// 會回不同清單——v1 只含穩定版，v1beta 才看得到 preview 模型，這正是我們加 ApiVersion 的動機。
    /// 不重試、不退避 — 這是診斷端點，即時失敗勝過拖延。
    /// </summary>
    public async Task<AiModelListResult> ListModelsAsync(CancellationToken ct = default)
    {
        var apiKey = await _credentials.GetApiKeyAsync(AiCredential.GeminiProvider, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AiModelListResult(false, Array.Empty<AiModelInfo>(), "Gemini API Key 尚未配置。");
        }

        var url = $"{BuildModelsCollectionUrl()}?key={Uri.EscapeDataString(apiKey)}&pageSize=200";

        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var errMsg = TryExtractErrorMessage(body) ?? body;
                _logger.LogWarning("ListModels HTTP {Status}：{Body}", (int)resp.StatusCode, body);
                return new AiModelListResult(false, Array.Empty<AiModelInfo>(),
                    $"ListModels HTTP {(int)resp.StatusCode}：{errMsg}");
            }

            var parsed = await resp.Content.ReadFromJsonAsync<GeminiModelsListResponse>(JsonOpts, ct).ConfigureAwait(false);
            var models = (parsed?.Models ?? Array.Empty<GeminiModelRecord>())
                .Select(m => new AiModelInfo(
                    Name: m.Name ?? string.Empty,
                    DisplayName: m.DisplayName,
                    Version: m.Version,
                    InputTokenLimit: m.InputTokenLimit,
                    OutputTokenLimit: m.OutputTokenLimit,
                    SupportedMethods: m.SupportedGenerationMethods ?? Array.Empty<string>()))
                .ToArray();
            return new AiModelListResult(true, models, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ListModels 請求異常。");
            return new AiModelListResult(false, Array.Empty<AiModelInfo>(), $"ListModels 請求異常：{ex.Message}");
        }
    }

    public void Dispose() => _http.Dispose();

    // ───── Gemini REST Wire Types (camelCase) ─────
    private sealed record GeminiRequest(
        [property: JsonPropertyName("contents")] GeminiContent[] Contents,
        [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig);

    private sealed record GeminiContent(
        [property: JsonPropertyName("parts")] GeminiPart[] Parts);

    private sealed record GeminiPart(
        [property: JsonPropertyName("text")] string Text);

    private sealed record GeminiGenerationConfig(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens);

    private sealed record GeminiResponse(
        [property: JsonPropertyName("candidates")] GeminiCandidate[]? Candidates,
        [property: JsonPropertyName("promptFeedback")] GeminiPromptFeedback? PromptFeedback);

    private sealed record GeminiCandidate(
        [property: JsonPropertyName("content")] GeminiContentRead? Content,
        [property: JsonPropertyName("finishReason")] string? FinishReason,
        [property: JsonPropertyName("safetyRatings")] GeminiSafetyRating[]? SafetyRatings);

    private sealed record GeminiContentRead(
        [property: JsonPropertyName("parts")] GeminiPart[]? Parts);

    private sealed record GeminiPromptFeedback(
        [property: JsonPropertyName("blockReason")] string? BlockReason,
        [property: JsonPropertyName("safetyRatings")] GeminiSafetyRating[]? SafetyRatings);

    private sealed record GeminiSafetyRating(
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("probability")] string? Probability,
        [property: JsonPropertyName("blocked")] bool? Blocked);

    private sealed record GeminiErrorResponse(
        [property: JsonPropertyName("error")] GeminiError? Error);

    private sealed record GeminiError(
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("status")] string? Status);

    private sealed record AiPayload(
        [property: JsonPropertyName("commentary")] string? Commentary,
        [property: JsonPropertyName("parameters")] IDictionary<string, JsonElement>? Parameters);

    // ───── ListModels Wire Types ─────
    private sealed record GeminiModelsListResponse(
        [property: JsonPropertyName("models")] GeminiModelRecord[]? Models);

    private sealed record GeminiModelRecord(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("inputTokenLimit")] long? InputTokenLimit,
        [property: JsonPropertyName("outputTokenLimit")] long? OutputTokenLimit,
        [property: JsonPropertyName("supportedGenerationMethods")] string[]? SupportedGenerationMethods);

    /// <summary>
    /// 單次模型嘗試的打包結果。<see cref="Diagnostic"/> 一定有值；<see cref="Result"/>
    /// 在 Ok 時是成功結果（Attempts 尚未填，由 GetAdviceAsync 統一 with 進去），失敗時是 UI 可顯示的 Failure 物件。
    /// </summary>
    private readonly record struct ModelTryOutcome(
        bool Succeeded,
        AiAttemptDiagnostic Diagnostic,
        AiAdviceResult? Result)
    {
        public static ModelTryOutcome Ok(AiAttemptDiagnostic diag, AiAdviceResult ok) => new(true, diag, ok);
        public static ModelTryOutcome Failed(AiAttemptDiagnostic diag, AiAdviceResult failure) => new(false, diag, failure);
    }
}
