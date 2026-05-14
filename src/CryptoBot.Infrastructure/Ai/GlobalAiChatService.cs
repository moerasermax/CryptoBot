using System.Text;
using CryptoBot.Application.Ai;
using CryptoBot.Application.Realtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoBot.Infrastructure.Ai;

/// <summary>
/// S74-C / S75：全域常駐 AI 對話 service 實作 — Scoped 生命週期、與 Blazor Server circuit 綁定。
///
/// S75 取代 S74-C 的 <c>gemini -p &quot;&lt;prompt&gt;&quot; --session-id &lt;uuid&gt;</c> 單發模式，
/// 改用 <see cref="IGeminiAcpClient"/>（<c>gemini --acp</c> 長連接 JSON-RPC 2.0 IPC）：
/// <list type="bullet">
///   <item>消除冷啟動延遲（per-prompt spawn ~1-3s → long-lived single process）。</item>
///   <item>解除 Win32 CreateProcess 命令列上限（28K wchar）— ACP 走 stdin pipe 不受限。</item>
///   <item>原生 session 持久化（ACP <c>session/new</c> sessionId 跨 prompt 共用）；C# 端 history 仍保留為 UI 渲染副本。</item>
/// </list>
///
/// 合約：<see cref="SendAsync"/> 永不拋；任何下游失敗（ACP / JSON / timeout）皆轉成
/// 含錯誤訊息的 ai 訊息 append 至 <see cref="History"/>，不破壞對話迴圈。
/// </summary>
public sealed class GlobalAiChatService : IGlobalAiChatService
{
    /// <summary>
    /// Phase 2 保留 sliding window 上限作為 token 控制保險（不再受 Win32 CreateProcess 限制）。
    /// 200K char 對應 ACP session 內合理 prompt 大小；超出此值的舊歷史會被 sliding window 從前端丟棄。
    /// Phase 4 整合測試後可重新評估或移除（session/new 後 ACP 內 history 已 cross-prompt 持久、C# 端理論可省略 history dump）。
    /// </summary>
    private const int MaxPromptChars = 200_000;

    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly IStrategyParameterKeyCatalog _keyCatalog;
    private readonly IGeminiAcpClient _acpClient;
    private readonly InteractiveCliAdvisorOptions _opts;
    private readonly ILogger<GlobalAiChatService> _logger;

    public GlobalAiChatService(
        IStrategyParameterKeyCatalog keyCatalog,
        IGeminiAcpClient acpClient,
        IOptions<InteractiveCliAdvisorOptions> opts,
        ILogger<GlobalAiChatService> logger)
    {
        _keyCatalog = keyCatalog;
        _acpClient = acpClient;
        _opts = opts.Value;
        _logger = logger;
    }

    public Guid SessionUuid { get; } = Guid.NewGuid();

    public IReadOnlyList<ChatMessage> History
    {
        get
        {
            lock (_history) return _history.ToArray();
        }
    }

    public event Action? HistoryChanged;

    public async Task SendAsync(string userText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userText)) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AppendUser(userText);

            string prompt;
            try
            {
                prompt = ComposePrompt(userText);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GlobalAiChat] ComposePrompt 失敗。");
                AppendAi($"⚠ Prompt 組裝失敗：{ex.Message}", payload: null, hasJson: false);
                return;
            }

            string raw;
            try
            {
                raw = await InvokeGeminiAsync(prompt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                AppendAi("（請求已取消）", payload: null, hasJson: false);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GlobalAiChat] InvokeGeminiAsync 失敗。");
                AppendAi($"⚠ gemini -p 失敗：{ex.Message}", payload: null, hasJson: false);
                return;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                AppendAi("（AI 無回應 — stdout 為空）", payload: null, hasJson: false);
                return;
            }

            TryParseAndAppendAi(raw);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ClearAsync()
    {
        lock (_history) _history.Clear();
        HistoryChanged?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 嘗試用共用 parser 解析 raw 字串為 (commentary, parameters)。
    /// 成功 → ai 訊息含結構化 payload、UI 顯示「🪄 套用參數」按鈕。
    /// 失敗 → 視為純對話、直接以 trim 後內容呈現。
    /// </summary>
    private void TryParseAndAppendAi(string raw)
    {
        var keys = _keyCatalog.AllParameterKeys;
        if (keys.Count > 0
            && AiAdvicePayloadParser.TryParse(raw, keys, out var commentary, out var parameters, out _))
        {
            var payload = new ApplyAiParametersUpdate(
                TargetStrategyKey: null, // Phase 3：未實作 strategyKey 萃取；BacktestLab 套當前 SelectedModel
                Parameters: parameters,
                Commentary: commentary);

            // commentary 為空時降級為原始 raw（剝除 fence 後）— 避免顯示空白訊息。
            var displayText = string.IsNullOrWhiteSpace(commentary)
                ? AiAdvicePayloadParser.ExtractJson(raw)
                : commentary;

            AppendAi(displayText, payload, hasJson: true);
            return;
        }

        AppendAi(raw.Trim(), payload: null, hasJson: false);
    }

    private void AppendUser(string text)
    {
        lock (_history)
        {
            _history.Add(new ChatMessage(
                Role: "user", Text: text, At: DateTimeOffset.UtcNow,
                HasJson: false, Payload: null));
        }
        HistoryChanged?.Invoke();
    }

    private void AppendAi(string text, ApplyAiParametersUpdate? payload, bool hasJson)
    {
        lock (_history)
        {
            _history.Add(new ChatMessage(
                Role: "ai", Text: text, At: DateTimeOffset.UtcNow,
                HasJson: hasJson, Payload: payload));
        }
        HistoryChanged?.Invoke();
    }

    /// <summary>
    /// 組合 prompt = system_preamble + sliding window history + new user msg。
    /// sliding window 從尾部往前累積，受 <see cref="MaxPromptChars"/> 約束；超界丟棄最舊輪次。
    /// 新 user 訊息（最末筆）已先 append 進 _history，所以從倒數第二筆往前取。
    /// </summary>
    private string ComposePrompt(string userText)
    {
        var preamble = BuildSystemPreamble();
        int budget = MaxPromptChars - preamble.Length - userText.Length - 200;

        var reversedLines = new List<string>();
        ChatMessage[] snapshot;
        lock (_history) snapshot = _history.ToArray();

        for (int i = snapshot.Length - 2; i >= 0; i--)
        {
            var m = snapshot[i];
            var line = $"{m.Role}: {m.Text}";
            if (budget - line.Length - 1 < 0) break;
            reversedLines.Add(line);
            budget -= line.Length + 1;
        }
        reversedLines.Reverse();

        var sb = new StringBuilder(preamble.Length + 1024);
        sb.AppendLine(preamble);
        if (reversedLines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 對話歷史");
            foreach (var line in reversedLines) sb.AppendLine(line);
        }
        sb.AppendLine();
        sb.Append("user: ").Append(userText);
        return sb.ToString();
    }

    private string BuildSystemPreamble()
    {
        var keys = string.Join(", ", _keyCatalog.AllParameterKeys);
        return
$@"你是 CryptoBot 的全域 AI 量化助理。請以對話形式輔助使用者分析市場、推敲策略參數。

## 回應模式（重要）
- 平時以自然語言回答；**不要強行包 JSON**。
- 當使用者明確請求「具體策略參數建議 / 網格設定」時，回應**內含一段 JSON code fence**：
  ```json
  {{
    ""commentary"": ""一句話定性（Trending/Ranging）+ 簡述設計理由"",
    ""parameters"": {{
      ""<合法 key>"": {{ ""min"": <decimal>, ""max"": <decimal>, ""step"": <decimal> }}
    }}
  }}
  ```
  使用者按「🪄 套用參數至實驗室」會把 parameters 灌入當前策略表單。
- 合法 parameter keys（區分大小寫、原字串照抄；跨多個策略 union）：{keys}
- 每個參數為三元組（step > 0；無須掃描時可 min=max、step=1）。
- 純對話回應時**禁絕** JSON code fence，避免 UI 誤判為可套用。";
    }

    /// <summary>
    /// S75：透過 <see cref="IGeminiAcpClient"/> 送 prompt、聚合 stream chunks 為完整 reply。
    ///
    /// Phase 2 行為：
    /// <list type="bullet">
    ///   <item>同步聚合 <see cref="IGeminiAcpClient.SendPromptAsync"/> yield 的 chunks 為單一 string。</item>
    ///   <item>逾時控制：以 <c>_opts.TimeoutSeconds</c> linked CTS 限制；逾時直接拋 <see cref="TimeoutException"/>，
    ///         上層 SendAsync 捕捉並轉為錯誤 ai 訊息。</item>
    ///   <item>無 cancel method（Phase 1 §7 親驗結論）— 逾時 / 取消僅在 C# 端中斷接收，
    ///         in-flight prompt 無法在 ACP 端取消、會繼續直到完成（資源浪費）；Phase 3 加固改為 dispose + 重新 ensure session。</item>
    /// </list>
    /// </summary>
    private async Task<string> InvokeGeminiAsync(string prompt, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));

        var sb = new StringBuilder();
        try
        {
            await foreach (var chunk in _acpClient.SendPromptAsync(prompt, timeoutCts.Token).ConfigureAwait(false))
            {
                sb.Append(chunk);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"GlobalAiChat 逾時（{_opts.TimeoutSeconds}s）— gemini --acp session/prompt 未在限時內完成。");
        }

        return sb.ToString();
    }
}
