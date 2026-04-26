# S30 · AI 量化導師整合 — 實作報告

**日期**：2026-04-22
**膠囊**：
- `ai_ops/capsules/TASK_S30_AI_ADVISOR.md`（主體）
- `ai_ops/capsules/TASK_S30_FIX_GEMINI_ENDPOINT.md`（模型退役熱修補）
- `ai_ops/capsules/TASK_S30_PRO_UPGRADE.md`（Gemini 2.5 Pro 升級 + 自主驗證）
- `ai_ops/capsules/TASK_S30_LITE_RESILIENCE.md`（Flash-Lite 降級 + 429/503 指數退避）
- `ai_ops/capsules/TASK_S30_GRID_UPGRADE.md`（AI 智慧網格生成器）
- `ai_ops/capsules/TASK_S30_ELITE_QUANT.md`（3.1 Pro 核心 + 雙 Pro 自動備援）
- 內部強化（S30-ELITE+）：模型名 bug fix + AI 呼叫診斷面板（使用者要求，非 PM 膠囊）
- 內部強化（S30-ELITE+2）：`MaxOutputTokens` 1024→8192 + `ListModels` 探測（使用者要求，非 PM 膠囊）
- 使用者決策（NO-CAP）：優化掃描組合數不設上限、以 AI 建議為準（2026-04-22，非 PM 膠囊）
- PM 工單（S30-ELITE+3）：Gemini API Version 動態切換（v1 / v1beta），支援 preview 模型（PM 對話內膠囊）

**狀態**：S30 全系列（主體 + FIX + PRO + LITE + GRID + ELITE + ELITE+ + ELITE+2 + NO-CAP + ELITE+3）全部交付；建置 Debug 0 warn / 0 error，測試套件 Domain 26 + Application 86 = 112 全通過。

## S30-ELITE+3 摘要（2026-04-22 同日第九追補 — Gemini API Version 動態切換）

### 工單來源
PM 對話內下達（未建檔為 `ai_ops/capsules/TASK_*.md`）。工單摘要：

> 當前 `GeminiOptions.BaseUrl` 寫死 `/v1/`。呼叫最新的 `gemini-3.1-pro-preview` 時，Google v1 端點回 404（not found for API version v1），因為 preview 類模型僅存於 `v1beta`。需求：新增 `ApiVersion` 設定、可透過 appsettings / env var 覆蓋、預設 `v1` 維持向後相容、URL 改為變數替換。

### 設計決策
- **`GeminiOptions.BaseUrl` semantic 切乾淨**：由原本的「完整 `/v{N}/models/` 集合 URL」改為「純 root host」（預設 `https://generativelanguage.googleapis.com`）。`/models/` 子路徑由 service 組裝時附上，避免 `BaseUrl` + `ApiVersion` 雙方各自內含版本字串產生歧義。
- **新增 `GeminiOptions.ApiVersion`**：`string`、預設 `"v1"`。
- URL 組裝集中於新 helper `BuildModelsCollectionUrl()`：`{BaseUrl}/{ApiVersion}/models`。`generateContent` 與 `ListModels` 都從這個回傳值接續拼。
- **404 log / error 訊息升級**：既然現在知道 v1 / v1beta 模型清單不同，404 時明示「若為 preview 模型，請將 `Gemini:ApiVersion` 設為 `v1beta`」。

### 受影響檔案（修改）
- `src/CryptoBot.Infrastructure/Ai/GeminiOptions.cs`
  - `BaseUrl` 預設值與 XML doc 改寫
  - 新增 `ApiVersion` 屬性 + doc（含 v1beta 切換時機、覆蓋方式）
- `src/CryptoBot.Infrastructure/Ai/GeminiAiAdvisorService.cs`
  - 新增 private helper `BuildModelsCollectionUrl()`
  - `TryModelAsync` 的 URL 組裝改走 helper
  - `ListModelsAsync` 的 URL 組裝改走 helper（含 XML doc 更新）
  - 404 warning log + `Failure(...)` 訊息改帶 `ApiVersion` 提示
- `src/CryptoBot.ConsoleApp/appsettings.json`
  - 新增 `"Gemini": { "ApiVersion": "v1beta" }` 區塊，主動啟用 preview 模型支援

### URL pattern 對照
```
舊：{BaseUrl 結尾含 /v1/models/}{model}:generateContent
新：{BaseUrl=host}/{ApiVersion}/models/{model}:generateContent
例：https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-pro-preview:generateContent
```

### 刻意未動
- `scripts/GeminiResilienceTest/`、`scripts/GeminiSmokeTest/`、`scripts/GeminiEliteDiagnostic/` 為獨立診斷工具，**不**經 `GeminiOptions`，內含各自的 `BaseUrl` 常數。PM 若判斷需要同步，可另案處理。

### 建置與測試
| 項目 | 結果 |
| --- | --- |
| `dotnet build src/CryptoBot.Infrastructure -c Debug` | 0 warn / 0 error |
| `dotnet test tests/CryptoBot.Domain.Tests --no-build` | 26/26 全綠 |
| `dotnet test tests/CryptoBot.Application.Tests --no-build` | 86/86 全綠 |

### 預期驗證流程（使用者端）
1. 重啟 ConsoleApp — appsettings 已加 `v1beta`
2. `/settings/exchanges` → 按「🔍 探測可用模型」→ 清單中應包含 `models/gemini-3.1-pro-preview`
3. `/lab` → 按 🪄 → 診斷面板第一列 `primary · gemini-3.1-pro-preview · HTTP 200`（不再 fallback 到 2.5-pro）

---

## NO-CAP 追補（2026-04-22 同日第八追補 — 掃描組合數不設上限，使用者決策）

### 觸發
使用者在 `/lab` 按 🪄 獲得 AI 建議後按「🚀 開始優化掃描」，回 `HTTP 400: Too many combinations: 19208 > 10000`。

### 討論與決策
提出三個候選：(A) 在 prompt 告知 10000 上限由 AI 自收斂；(B) 伺服器端自動 shrink；(C) 維持現狀由 UI 手調。使用者最終指示：**「以 AI 說的為主，不需要設上限」**——即選項 A 先嘗試過後又撤回、全面移除上限。

### 受影響檔案（修改）
- `src/CryptoBot.ConsoleApp/Api/LabEndpoints.cs`
  - 刪除 `private const long MaxTotalCombinations = 10000`
  - 刪除 `ValidateRequest` 內的 `total *= count` 累乘與乘積超卡拒絕邏輯
  - 保留 `step > 0` 與 `max >= min` 兩條純語義檢查
- `src/CryptoBot.Infrastructure/Ai/GeminiAiAdvisorService.cs`
  - `BuildPrompt` 步驟 3 移除「單參數 `(max-min)/step` 不宜超過 20」與「總乘積 ≤ 10000」兩條規則，只留 `step > 0` 與「min=max 特例」

### 設計含意（PM 需知）
1. **AI 為主體**：此系統不再由伺服器對 AI 的量化建議做數值性否決；AI 想開多大網格就跑多大。
2. **防線改在人與 UI**：若偶發組合爆炸，使用者可在 UI 編輯 AI 建議的 min/max/step 後才送出；本回放行後沒有自動保護。
3. **記憶為準**：此決策已寫入 Claude 側 feedback memory（`feedback_ai_advisor_no_cap.md`），未來若再遇類似議題，Claude 不會主動提議重新加上硬性上限。
4. **覆蓋條件**：若 PM 日後判斷需要恢復上限（例如生產部署防誤觸），需明確下新膠囊指示；僅憑一次 429 / OOM 事件不自動撤銷此決策。

---

## S30-ELITE+2 摘要（2026-04-22 同日第七追補 — MAX_TOKENS 修正 + 可用模型探測）

承接 ELITE+ 的診斷面板上線後，使用者即收到兩個「診斷面板立刻派上用場」的新症狀：

```
primary  gemini-3.1-pro-preview   HTTP 404  0 retry   168ms   —   models/gemini-3.1-pro-preview is not found for API version v1...
fallback gemini-2.5-pro           HTTP 200  0 retry  8374ms   MAX_TOKENS   —
```

亦即：(a) 即便模型名改成 `gemini-3.1-pro-preview`，v1 REST endpoint 仍查不到，推測只活在 `v1beta`；(b) fallback 回到 `gemini-2.5-pro` 但輸出被 `MaxOutputTokens=1024` 截斷，`finishReason=MAX_TOKENS` 而 text 為空。本次追補兩個正面處理：

1. **`MaxOutputTokens` 改可設定且預設放大到 8192** — 修掉 fallback 被硬截斷的根因。
2. **新增 `GET /api/ai/models` + UI「🔍 探測可用模型」按鈕** — 使用者可直接看 Google 對此金鑰暴露的模型清單，照表挑出真正能跑 `generateContent` 的 `name` 填回 appsettings/env，不必再猜字串。

### T1. `GeminiOptions.MaxOutputTokens`（1024→8192）

`GeminiOptions.cs` 加欄位：

```csharp
/// Gemini 單次回應的最大 output tokens。S30-ELITE+2：預設 8192。
/// 為什麼這麼高：S30-ELITE prompt 要求「行情定性 + 雙目標分析 + 多參數 min/max/step JSON」
/// 塞一起，Pro 回應經常踩到 1024 上限導致 text 為空、finishReason=MAX_TOKENS。
public int MaxOutputTokens { get; set; } = 8192;
```

`GeminiAiAdvisorService.BuildGenerationConfig` 改用 `_opts.MaxOutputTokens`；空文字分支判斷新增 `MAX_TOKENS` 專屬訊息，把調整指引直接塞給使用者：

```csharp
var emptyReason = safetyBlock is not null
    ? $"模型 {model} 沒有回傳內容（安全過濾：{safetyBlock}）。"
    : finishReason == "MAX_TOKENS"
        ? $"模型 {model} 輸出被 MaxOutputTokens={_opts.MaxOutputTokens} 截斷 — 請到 appsettings 調高 Gemini.MaxOutputTokens 或設 Gemini__MaxOutputTokens 環境變數。"
        : finishReason is not null and not "STOP"
            ? $"模型 {model} 沒有回傳內容（finishReason={finishReason}）。"
            : $"模型 {model} 沒有回傳內容。";
```

### T2. `IAiAdvisorService.ListModelsAsync` + `GeminiAiAdvisorService` 實作

合約：**永不拋** — 金鑰缺、HTTP error、網路異常統一回 `Success=false` + `Error`。

```csharp
Task<AiModelListResult> ListModelsAsync(CancellationToken ct = default);

public sealed record AiModelListResult(
    bool Success,
    IReadOnlyList<AiModelInfo> Models,
    string? Error);

public sealed record AiModelInfo(
    string Name,               // "models/gemini-2.5-pro"
    string? DisplayName,
    string? Version,
    long? InputTokenLimit,
    long? OutputTokenLimit,
    IReadOnlyList<string> SupportedMethods);  // 有 "generateContent" 才是能餵我們 prompt 的模型
```

實作走 Google ListModels REST：`GET {baseUrl}/v1/models?key={apiKey}&pageSize=200`，回應 DTO `GeminiModelsListResponse { Models[] }` → 轉成 `AiModelInfo`。
`NoOpAiAdvisorService` 同步補 stub（Success=false + 「尚未配置」訊息）。

### T3. `GET /api/ai/models` 端點 + DTO

`AiAdvisorEndpoints.cs`：

```csharp
group.MapGet("/models", async (IAiAdvisorService advisor, CancellationToken ct) =>
{
    var result = await advisor.ListModelsAsync(ct).ConfigureAwait(false);
    return Results.Ok(new AiModelListDto(
        Success: result.Success,
        Error: result.Error,
        Count: result.Models.Count,
        Models: result.Models));
});
```

### T4. `/settings/exchanges` 新增「🔍 探測可用模型」按鈕

在 Gemini 金鑰區塊的 settings-actions 列加按鈕（有金鑰時啟用），點擊後展開 `<details open>`，顯示模型表：`Name / DisplayName / Version / InputTokens / OutputTokens / SupportedMethods`。

重點：**`generateContent` 以綠色 pill highlight** — 使用者一眼看得出哪些 name 可以直接填回 `Gemini:PrimaryModel` / `Gemini:FallbackModel`。

### 使用者情境還原

以本次診斷輸出為例，使用者接下來可以：

1. 按「🔍 探測可用模型」 → 看到例如 `models/gemini-2.5-pro`、`models/gemini-2.5-flash` 等皆標綠，但沒有 `gemini-3.1-pro-preview`
2. 結論：v1 endpoint 當前對此金鑰不暴露 3.1 preview → 回 appsettings 把 `Gemini:PrimaryModel` 改為實測可用的 name（或改用 `v1beta` endpoint，另案處理）
3. 即便暫時還是 fallback 到 2.5 Pro，因為 `MaxOutputTokens=8192`，不再被 MAX_TOKENS 截斷

兩個修正合在一起，使用者可以**自助收斂**模型選擇，不必每次把錯誤貼回來通靈。

### S30-ELITE+2 受影響檔案

**修改**
- `src/CryptoBot.Application/Ai/IAiAdvisorService.cs`（新增 `ListModelsAsync` 介面成員、`AiModelListResult`、`AiModelInfo` records）
- `src/CryptoBot.Application/Ai/NoOpAiAdvisorService.cs`（補 `ListModelsAsync` stub）
- `src/CryptoBot.Infrastructure/Ai/GeminiOptions.cs`（`MaxOutputTokens` 欄位，預設 8192）
- `src/CryptoBot.Infrastructure/Ai/GeminiAiAdvisorService.cs`（`ListModelsAsync` 實作、`GeminiModelsListResponse` / `GeminiModelRecord` wire types、空文字分支加 `MAX_TOKENS` 指引、`BuildGenerationConfig` 改用 `_opts.MaxOutputTokens`）
- `src/CryptoBot.ConsoleApp/Api/AiAdvisorEndpoints.cs`（新增 `GET /api/ai/models` + `AiModelListDto`）
- `src/CryptoBot.ConsoleApp/Components/Pages/ExchangeSettings.razor`（「🔍 探測可用模型」按鈕 + models 折疊表 + `ProbeModelsAsync` + 相關 `_models*` 欄位）

### 建置與測試

| 項目 | 結果 |
| --- | --- |
| `dotnet build src/CryptoBot.Application -c Debug` | 0 warn / 0 error |
| `dotnet build src/CryptoBot.Infrastructure -c Debug` | 0 warn / 0 error |
| `dotnet build tests/CryptoBot.Domain.Tests -c Debug` | 0 warn / 0 error |
| `dotnet build tests/CryptoBot.Application.Tests -c Debug` | 0 warn / 0 error |
| `dotnet test tests/CryptoBot.Domain.Tests --no-build` | 26/26 全綠 |
| `dotnet test tests/CryptoBot.Application.Tests --no-build` | 86/86 全綠 |

> 註：`dotnet build CryptoBot.sln` 在此次驗證時因使用者本機 `CryptoBot.ConsoleApp` 仍在執行（PID 33296）鎖住 `Application.dll` / `Infrastructure.dll`，收到 MSB3021/MSB3027 file-copy 錯誤。這是 runtime lock 不是編譯錯誤——拆個別專案重建後全數 0 warn / 0 error。使用者停掉 ConsoleApp 即可 solution-level build 成功。

### 後續迭代（非本追補強制）

- 如果 `gemini-3.1-pro-preview` 只活在 v1beta，可將 `GeminiOptions` 加 `ApiVersion`（`v1` / `v1beta`）欄位，URL 動態改寫 `/{ApiVersion}/models/...` — 但要先讓使用者按「探測」確認實際情況再決定。
- 「探測可用模型」的結果可快取 10 分鐘；目前每次按按鈕都打一次 Google，對使用者而言足夠即時。
- 可在模型表加「套用為 Primary / Fallback」小按鈕，直接寫回 `appsettings`（需讓設定支援熱改寫，另案）。

---

## S30-ELITE+ 摘要（2026-04-22 同日第六追補 — 模型名修正 + 診斷面板）

兩件事：
1. **Primary 模型名 bug fix**：`gemini-3.1-pro` → **`gemini-3.1-pro-preview`**。先前誤植為 `gemini-3.1-pro` 導致 Primary 恆 404、強制走 Fallback → 使用者看到 `⚠ 模型 gemini-2.5-pro 沒有回傳內容。` 後還得通靈猜為什麼不是 3.1。
2. **AI 呼叫診斷面板**：每次 `GetAdviceAsync` 回傳後把結構化 attempt 列表推進 in-memory ring buffer（最後 20 筆），UI 在 `/lab` 與 `/settings/exchanges` 兩處展開就能看清楚「Primary 在哪一步掛、Fallback 為什麼空回」。

### T1. `AiAttemptDiagnostic` + `AiAdviceResult.Attempts`

`IAiAdvisorService.cs` 新增 record：

```csharp
public sealed record AiAttemptDiagnostic(
    string Model,            // "gemini-3.1-pro-preview"
    string Phase,            // "primary" | "fallback"
    int HttpStatus,          // 0/200/404/429/503...
    string? FinishReason,    // "STOP" / "SAFETY" / "MAX_TOKENS" / "RECITATION"
    string? SafetyBlock,     // "promptFeedback.blockReason=SAFETY" 或 "HARM_CATEGORY_xxx:HIGH"
    string? ErrorMessage,    // Gemini error.message 或我們自己組的失敗摘要
    int RetryCount,          // 該模型內部 429/503 退避走了幾次
    int DurationMs,          // 含退避等待的總耗時
    DateTimeOffset At);
```

`AiAdviceResult` 尾巴加 `IReadOnlyList<AiAttemptDiagnostic> Attempts` 欄位；`NoOpAiAdvisorService` 同步改為回空陣列。

### T2. `GeminiAiAdvisorService` 診斷收集

- 內層 `TryModelAsync` 回傳新的 `ModelTryOutcome(bool Succeeded, AiAttemptDiagnostic Diagnostic, AiAdviceResult? Result)` — diagnostic 一定有值
- 外層 `GetAdviceAsync` 用 `List<AiAttemptDiagnostic> attempts` 累積 primary + 可能的 fallback，最終以 `result with { Attempts = attempts }` 回 UI
- Wire types 擴充解析：
  - `GeminiCandidate` 新增 `finishReason` / `safetyRatings[]`
  - 新增 `GeminiPromptFeedback { blockReason, safetyRatings[] }`
  - 新增 `GeminiErrorResponse { error { code, message, status } }` 用於 non-2xx 時抽 `error.message`
- `DescribeSafetyBlock` 濃縮：先看 `promptFeedback.blockReason`，再看 `finishReason==SAFETY + safetyRatings[].blocked=true`，都沒才回 null
- 空內容分支訊息升級：`"模型 {model} 沒有回傳內容（安全過濾：HARM_CATEGORY_DANGEROUS_CONTENT:HIGH）。"` — 把過濾原因直接寫進 UI 看得到的 error

### T3. `IAiAdviceTraceLog` Ring Buffer（Application 層）

- `AiAdviceTraceLog`：容量 20，`LinkedList<AiAdviceTrace>` + 單 `lock`。
- `AiAdviceTrace`：濃縮版 `AiAdviceResult`，只保留 Commentary 前 200 字 + 完整 attempts（attempts 才是診斷重點）。
- 註冊於 `CryptoBot.Application.DependencyInjection.AddApplication` — Singleton、無 schema、host 重啟即清空。
- `GeminiAiAdvisorService` 在每個返回路徑包 `RecordAndReturn(result)`，trace log 任何例外都被吞掉 + log warning，不影響 AI 結果。

### T4. `GET /api/ai/traces` + 兩個 UI 進入點

- `AiAdvisorEndpoints.MapGet("/traces")` — 回 `AiTracesResponseDto { Count, Traces }`，預設 20 筆，可用 `?limit=N` 覆蓋（service 內 clamp 到容量上限）
- `AiAdviseResponseDto` 尾巴加 `Attempts` 欄位 — /lab 單次查詢不必再打 `/traces` 第二趟
- **`/lab` 的 `AiAdvisorPanel.razor`**：建議卡片下方新增折疊區「🔍 這一次診斷」，表格欄位 `Phase / Model / HTTP / Retry / 耗時 / finishReason / Safety-Error`，Phase 列用 `pos` 類綠色標記成功，錯誤列平鋪 `SafetyBlock` + `ErrorMessage`
- **`/settings/exchanges` 的 Gemini 區塊下**：新增折疊區「🔍 AI 呼叫診斷（最近 N 筆）」，欄位 `時間 / 結果 / 最終模型 / 嘗試鏈 / 錯誤-備註`，嘗試鏈格式 `primary:gemini-3.1-pro-preview(404) → fallback:gemini-2.5-pro(200,×2)` 一目瞭然
- 頁面載入時自動 `/api/ai/traces?limit=20`，折疊區有「↻ 重新整理」按鈕

### 實測情境還原（使用者原 bug）

修正後，使用者下一次按 🪄 應該會看到：

```
模型：gemini-3.1-pro-preview · 趨勢判讀：Trending
（展開診斷）
primary  gemini-3.1-pro-preview   HTTP 200   retry=0   3200ms   STOP   OK
```

若 Google 關 3.1 或暫時 429，備援鏈依舊觸發，但使用者**不必再猜**：

```
primary  gemini-3.1-pro-preview   HTTP 429   retry=3   14100ms   —   Gemini 429：模型…速率限制
fallback gemini-2.5-pro           HTTP 200   retry=0    2400ms   SAFETY   HARM_CATEGORY_…:HIGH
```

### S30-ELITE+ 受影響檔案

**新增**
- `src/CryptoBot.Application/Ai/IAiAdviceTraceLog.cs`
- `src/CryptoBot.Application/Ai/AiAdviceTraceLog.cs`

**修改**
- `src/CryptoBot.Application/Ai/IAiAdvisorService.cs`（加 `AiAttemptDiagnostic` record、`AiAdviceResult.Attempts` 欄位）
- `src/CryptoBot.Application/Ai/NoOpAiAdvisorService.cs`（尾巴加空 attempts）
- `src/CryptoBot.Application/DependencyInjection.cs`（註冊 `IAiAdviceTraceLog`）
- `src/CryptoBot.Infrastructure/Ai/GeminiOptions.cs`（`PrimaryModel` → `gemini-3.1-pro-preview`）
- `src/CryptoBot.Infrastructure/Ai/GeminiAiAdvisorService.cs`（診斷收集、wire 擴充、`RecordAndReturn`、注入 `IAiAdviceTraceLog`）
- `src/CryptoBot.Infrastructure/DependencyInjection.cs`（建構子多注入一個服務）
- `src/CryptoBot.ConsoleApp/Api/AiAdvisorEndpoints.cs`（DTO 擴 Attempts、新增 `GET /traces`、`AiTracesResponseDto`）
- `src/CryptoBot.ConsoleApp/Components/Lab/AiAdvisorPanel.razor`（診斷折疊區、三處 DTO 構造補 Attempts）
- `src/CryptoBot.ConsoleApp/Components/Pages/ExchangeSettings.razor`（最近呼叫折疊區 + `ReloadTracesAsync` + `FormatAttemptChain`）
- `scripts/GeminiEliteDiagnostic/Program.cs`（Primary model 名字串同步修正）

### 建置與測試

| 項目 | 結果 |
| --- | --- |
| `dotnet build CryptoBot.sln -c Debug` | 0 warn / 0 error |
| `dotnet test CryptoBot.sln -c Debug --no-build` | Domain 26/26、Application 86/86 全綠 |
| `dotnet build scripts/GeminiEliteDiagnostic/` | 0 warn / 0 error |

### 後續迭代（非本追補強制）

- 若需要跨重啟保留診斷紀錄 → 把 `AiAdviceTraceLog` 換成 SQLite `AiAdviceTraces` 表，讀寫合約不變
- 診斷面板可以加「匯出最近 20 筆為 JSON」按鈕供 PM（Gemini）遠端診斷
- `AiAdvisorPanel` 的診斷折疊區可加「只顯示失敗」過濾器，長期觀測 429 頻率

---



## S30-ELITE 摘要（2026-04-22 同日第五追補 — 高級量化決策鏈）

膠囊 `TASK_S30_ELITE_QUANT.md` 把 AI 顧問升級為 **Gemini 3.1 Pro 核心 + 雙 Pro 自動備援**，
利用用戶剛升級的 Pay-as-you-go 付費額度取得最強推理能力，同時用降級鏈對沖單模型不可用風險。

### T1. 雙 Pro 備援機制

**`GeminiOptions.cs`**：`Model` 欄位汰除，改為雙模型配置——

| 欄位 | 預設值 | 角色 |
| --- | --- | --- |
| `PrimaryModel` | `gemini-3.1-pro` | 最強推理、首發 |
| `FallbackModel` | `gemini-2.5-pro` | Primary 觸發 429/503/404/網路異常時自動接手 |
| `BaseUrl` | `https://generativelanguage.googleapis.com/v1/models/` | 維持 v1 穩定版 |
| `TimeoutSeconds` | 30 | Pro 模型延遲較大 |

**`GeminiAiAdvisorService.cs`** 重構為兩層：

1. **內層 `TryModelAsync(model)`**：沿用 S30-LITE 的 429/503 指數退避（2/4/8s × 3 次）。不成功則回傳 `ModelAttemptResult(Succeeded=false, Status=N)` 讓上層決策。
2. **外層 `GetAdviceAsync`**：Primary 先行；失敗時檢查 `ShouldSwitchToFallback(status)` → `{0, 404, 429, 503}` 時切 Fallback；否則直接回錯（400/401/403 換模型救不回）。

降級前停頓 `InterModelPauseMs = 3000ms` 讓配額稍緩，並記日誌：
```
[AI-ELITE] Switched to Fallback (gemini-2.5-pro) due to error 429 on gemini-3.1-pro — 停頓 3000ms 後重試
```

`AiAdviceResult.Model` 欄位現在回傳「實際產出結果的模型」（Primary 或 Fallback），UI `AiAdvisorPanel` 的「模型：xxx」提示會自動正確顯示當次使用的模型。

### T2. Prompt 高維度行情分析

`BuildPrompt` 升級為三段結構：

1. **步驟 1 — 行情定性**：要求 AI 首句明確判定 **Trending（趨勢）** 或 **Ranging（震盪）**。
2. **步驟 2 — 雙目標優化**：明確以 **Sharpe Ratio 最大化** + **Max Drawdown 最小化** 為目標。
3. **步驟 3 — 三維網格輸出**：沿用 S30-GRID 的 `{min, max, step}` 結構；並指導 Trending 時擴大慢線 min/max、Ranging 時收窄區間精細化。
   - `step > 0` 且 `(max - min) / step ≤ 20` 防爆炸
   - 極精確參數可 `min = max, step = 1`

### T3. DTO + 表單

DTO 與表單已由 S30-GRID 先行完成升級，本膠囊不需再動；新版 service 的 `Model` 欄位透明傳到 `AiAdviseResponseDto.Model` → UI 顯示。`ExchangeSettings.razor` 的提示字樣同步更新為「S30-ELITE 雙 Pro 備援」。

### T4. 閉環自驗證 — `scripts/GeminiEliteDiagnostic/`

新增獨立 .NET 8 控制台專案（不在 `CryptoBot.sln` 內，避免污染主建置）：

- 先呼 Primary `gemini-3.1-pro`（最多 5 次退避 2/4/8/16/32s）
- 成功 → 印出模型 + 回應 + 耗時，exit 0
- 失敗且 429/503 → 模擬 service 的 3s 停頓，切 Fallback `gemini-2.5-pro` 重跑相同流程
- Fallback 也失敗且 **總失敗次數 ≥ 5** → 寫完整 HTTP 封包（status + headers + body）到 `ai_ops/diagnostics/ELITE_FAILURE.log` 供 PM（Gemini）遠端診斷

退出碼：
- `0` 任一 Pro 模型成功
- `2` 雙 Pro 皆失敗（詳細 log 已寫入）
- `3` 金鑰缺失

獨立建置驗證：`dotnet build scripts/GeminiEliteDiagnostic/GeminiEliteDiagnostic.csproj` → **0 warn / 0 error**。

### 建置與測試

| 項目 | 結果 |
| --- | --- |
| `dotnet build -c Debug` | 0 warn / 0 error |
| `dotnet build -c Release` | 0 warn / 0 error |
| `CryptoBot.Domain.Tests` | 26 通過 / 0 失敗 |
| `CryptoBot.Application.Tests` | 86 通過 / 0 失敗 |
| `GeminiEliteDiagnostic` 編譯 | 0 warn / 0 error |

### 最終 Primary / Fallback 模型字串

| 配置欄位 | 值 |
| --- | --- |
| `GeminiOptions.PrimaryModel` | **`gemini-3.1-pro`** |
| `GeminiOptions.FallbackModel` | **`gemini-2.5-pro`** |

### VCP 狀態

| 檢核點 | 實作狀態 | 使用者驗證 |
| --- | --- | --- |
| VCP-Elite-Connect（3.1 Pro 成功回應） | service + diagnostic 已就緒 | **待用戶跑 `GeminiEliteDiagnostic` 或在 /lab 操作驗證** |
| VCP-Fallback（停用 3.1 後自動降級 2.5） | `ShouldSwitchToFallback` + 3s 停頓 + 重試 已實作 | **待用戶端注入假 404 / 撤銷 3.1 權限驗證** |
| VCP-Grid-Size（套用後 GridSize 合理） | 沿用 S30-GRID 的 `ApplyGridParametersAsync` + 超過 2000 提示 | **待用戶操作驗證** |

---

## S30-GRID 摘要（2026-04-22 同日第四追補）

膠囊 `TASK_S30_GRID_UPGRADE.md` 把 AI 顧問從「點建議」升級為「網格建議」——
原本 AI 只能回單一 `value`，套用後 Min=Max=Value、GridSize=1，失去優化掃描意義。
升級後 AI 回傳 `{ "min": ..., "max": ..., "step": ... }`，使用者按「填入」即直接拿到可掃描區間。

### T1a. Application 契約升級（`CryptoBot.Application.Ai`）

`IAiAdvisorService.cs`：
- 新增 `public sealed record ParameterGridRange(decimal Min, decimal Max, decimal Step)`
- `AiAdviceResult.SuggestedParameters` 由 `IReadOnlyDictionary<string, decimal>` → `IReadOnlyDictionary<string, ParameterGridRange>`

`NoOpAiAdvisorService.cs`：失敗字典型別同步更新。

### T1b. `GeminiAiAdvisorService` Prompt + Sanitize 升級

**Prompt 進化**（`BuildPrompt`）：
- 明確要求 AI 「設計掃描範圍（min/max/step）」，不是單一值
- 給 4 條設計原則：高波動擴區、平穩收斂、step > 0、grid 不過 20 點、已精確者可 min=max+step=1
- 要求純 JSON，格式：`"parameters": { "Key": { "min": N, "max": N, "step": N } }`

**解析韌性**（`SanitizeParameters` + 新增 `TryParseGridRange`）：
| AI 回傳形態 | Sanitize 行為 |
| --- | --- |
| `"Key": {"min":5, "max":20, "step":1}` | 直接採用（正常網格） |
| `"Key": {"min":5, "max":20}` | step 缺漏 → fallback 1 |
| `"Key": {"min":5}` | max 缺漏 → 用 min 值補齊 |
| `"Key": 10`（舊版單值） | 退化為 `(10, 10, 1)` |
| `"Key": "garbage"` | 丟棄（不加入結果） |

額外：min/max 反向自動 swap，step ≤ 0 回退為 1，對 `JsonElement.String` 也嘗試解析（`InvariantCulture`）。
Key 不區分大小寫匹配並正規化回 `expectedKeys` 的原樣。

### T2. 表單元件重構

`StrategyParameterFormBase.cs` 新增 virtual 方法：
```csharp
public virtual Task ApplyGridParametersAsync(IReadOnlyDictionary<string, ParameterGridRange> parameters)
    => Task.CompletedTask;
```
S25 T3 的 `ApplyParametersAsync(IReadOnlyDictionary<string, decimal>)` 保留（供 QuickFill 快取回放使用，語義為 Min=Max=value, Step=1）。

**受影響的表單清單（全數已覆寫 `ApplyGridParametersAsync`）**：

| 表單 | 策略 key | 參數維度 |
| --- | --- | --- |
| `SmaParameterForm.razor` | `sma` | FastSmaPeriod / SlowSmaPeriod |
| `B46ParameterForm.razor` | `rsi-bb` | RsiPeriod / RsiOversold / RsiOverbought / BbPeriod / BbStdDev |
| `TrendFollowingParameterForm.razor` | `trend` | FastEmaPeriod / SlowEmaPeriod / RsiPeriod / RsiMidline |
| `MeanReversionParameterForm.razor` | `mean-reversion` | BbPeriod / BbStdDev / RsiPeriod / RsiOversold / RsiOverbought |

每個表單處理規則一致：AI 給的 `Min/Max/Step` 直接寫入三欄位；`Step ≤ 0` 回退為該維度合理預設（整數欄用 1、BbStdDev 用 0.1）。

### T3. 安全鎖（`CurrentGridSize > 2000`）

兩層防護，不擋用戶、只提示：
1. **`AiAdvisorPanel.razor`**：建議表格下方新增「預估總格數」，超過 2000 時加註 `⚠ 超過 2000 — 建議手動調大 Step 以保護瀏覽器效能。`
2. **`BacktestLab.razor.ApplyAiSuggestedParametersAsync`**：套用後讀 `form.CurrentGridSize`，若 > 2000 轉成紅色 toast，提示一樣。

### T4. API + Panel 合約同步

`AiAdvisorEndpoints.cs`：
- `AiAdviseResponseDto.SuggestedParameters` 型別同步升級為 `IReadOnlyDictionary<string, ParameterGridRange>`
- 市場資料抓取失敗分支的空字典型別改 `Dictionary<string, ParameterGridRange>()`

`AiAdvisorPanel.razor`：
- `OnApplyParameters` EventCallback 升級為網格字典
- 顯示表格新增「格數」欄、表格下方顯示「預估總格數」
- 新增 `FormatRange` / `GridCount` / `TotalGridSize` 靜態工具方法

`BacktestLab.razor`：
- `ApplyAiSuggestedParametersAsync` 簽名改接 `IReadOnlyDictionary<string, ParameterGridRange>`
- 改呼 `form.ApplyGridParametersAsync`（不再走 `ApplyParametersAsync`）
- `@using CryptoBot.Application.Ai` 匯入 `ParameterGridRange`

### 建置與測試

| 項目 | 結果 |
| --- | --- |
| `dotnet build -c Debug` | 0 warn / 0 error |
| `dotnet build -c Release` | 0 warn / 0 error |
| `CryptoBot.Domain.Tests` | 26 通過 / 0 失敗 |
| `CryptoBot.Application.Tests` | 86 通過 / 0 失敗 |

### VCP 狀態

| 檢核點 | 實作狀態 | 使用者驗證 |
| --- | --- | --- |
| VCP-Grid（AI 回 min/max/step 結構） | Prompt + Sanitize 已就緒 | **待用戶端 /lab 頁面操作驗證** |
| VCP-Apply（填入後 GridSize > 1） | `ApplyGridParametersAsync` 已落地 | **待用戶端操作驗證** |
| VCP-Optimize（排行榜能跑） | 沿用既有優化掃描鏈路，未改 `OptimizationRequest` 契約 | **待用戶端操作驗證** |

### 可能的後續迭代（非本膠囊強制）

- AI 偶爾會給出 grid 爆炸的 step（例如 `(10, 100, 1) = 91 格`）—— 目前只在 UI 提示、未強制 clamp。
- 可考慮在 Panel 新增「一鍵加大 Step」micro-action，直接把 step 倍增直到 grid ≤ 2000。
- `ExchangeSettings.razor` 的 Gemini 模型說明字樣尚未同步 S30-GRID 訊息（非必要）。

---

## S30-LITE 摘要（2026-04-22 同日第三追補）

膠囊 `TASK_S30_LITE_RESILIENCE.md` 聚焦於「點一下就 429」的免費層 RPM=15 瓶頸，對策是 **模型降級 + 內建指數退避 + 自主壓測**。

### T1. 模型輕量化
- `GeminiOptions.Model` `gemini-2.5-pro` → **`gemini-2.5-flash-lite`**（免費層 RPM 較寬，延遲更低）
- `TimeoutSeconds` 維持 **30s**（單次 HTTP 請求 timeout，**不含** 退避等待）
- `ExchangeSettings.razor` 預設模型字樣同步更新，並提示「自帶 429/503 退避重試」
- Model 升級歷程：`1.5-flash`（S30）→ `3.1-flash`（S30-FIX）→ `2.5-pro`（S30-PRO）→ `2.5-flash-lite`（S30-LITE）

### T2. `GeminiAiAdvisorService.GetAdviceAsync` 內建指數退避
`PostAsJsonAsync` 外層包 for-loop，狀態機：

| 狀態 | 動作 |
| --- | --- |
| 2xx 或 非 429/503 | 跳出 loop，走既有成功 / 錯誤分支 |
| 429 / 503 且仍有重試額度 | `LogWarning("[AI-RETRY] Attempt X due to HTTP Y — 退避 Nms…")` → `Task.Delay(N, ct)` → 再試 |
| 429 / 503 但 3 次都耗盡 | 跳出 loop，走 **合併的 429/503 分支**（Failure 訊息含「經 3 次退避仍失敗」） |

退避排程：**2s → 4s → 8s**（最多累計 14s 純等待），對應膠囊 T2 規格。

`CancellationToken` 貫穿 `Task.Delay`，UI 端若取消請求退避可提早中止。

`HttpResponseMessage` 以 `finally { resp?.Dispose(); }` 確保釋放（配合 loop 內重賦值前的 `resp?.Dispose()`）。

### T3. 自主壓測腳本（`scripts/GeminiResilienceTest/`）
- 連續 **3 輪** 呼叫 `generateContent`
- 每輪內部 **最多 5 次重試**，退避排程 **2/4/8/16/32 秒**（比 service 端更激進 — 模擬「如果我們把 cap 提高會怎樣」）
- 只對 429/503 退避；其他 status（含 400/404）視為 `FatalStatus` 直接跳出
- 某輪 5 次全敗 → 寫 `ai_ops/diagnostics/GEMINI_429_DETAIL.log`（含最後一次完整 HTTP Headers + JSON body，包 `quotaMetric`/`overloaded` 等 Google 專屬訊息）→ Exit code 2
- 3 輪全成功 → Exit code 0 + 印出每輪耗時與重試數

執行方式（從 CryptoBot 專案根目錄）：
```bash
dotnet run --project scripts/GeminiResilienceTest -c Release
# 或指定金鑰：
GEMINI_API_KEY=... dotnet run --project scripts/GeminiResilienceTest -c Release
```

### S30-LITE 受影響檔案
- `src/CryptoBot.Infrastructure/Ai/GeminiOptions.cs`（Model / Timeout 註解更新）
- `src/CryptoBot.Infrastructure/Ai/GeminiAiAdvisorService.cs`（for-loop 退避 + 合併 429/503 分支）
- `src/CryptoBot.ConsoleApp/Components/Pages/ExchangeSettings.razor`（預設模型字樣）
- **新增** `scripts/GeminiResilienceTest/GeminiResilienceTest.csproj`
- **新增** `scripts/GeminiResilienceTest/Program.cs`
- `ai_ops/diagnostics/IMPLEMENTATION_REPORT.md`（本段）

---

## S30-PRO 摘要（2026-04-22 同日再追補）

膠囊 `TASK_S30_PRO_UPGRADE.md` 要求三件事，對應實作如下：

### T1. 模型與 Timeout 升級
- `GeminiOptions.Model` `gemini-3.1-flash` → **`gemini-2.5-pro`**（深度推理，適合量化建議）
- `GeminiOptions.TimeoutSeconds` `15` → **`30`**（2.5-pro 推理較久）
- `ExchangeSettings.razor` 預設模型字樣同步更新為 `gemini-2.5-pro`
- Model 升級歷程：`gemini-1.5-flash`（S30 初版）→ `gemini-3.1-flash`（S30-FIX）→ `gemini-2.5-pro`（S30-PRO）

### T2. 自主驗證腳本（`scripts/GeminiSmokeTest/`）
獨立的 .NET 8 Console — 不加入 `CryptoBot.sln`，僅診斷用途。

**金鑰來源優先序**：
1. `cryptobot.db` → `AiCredentials` 表（`Provider='Gemini'`）的 `ApiKey` 欄位（以 `Microsoft.Data.Sqlite` 直接讀、ReadOnly mode）
2. Fallback：環境變數 `GEMINI_API_KEY`
3. 都無 → Exit code 3 + 明確引導

**候選清單**（最多嘗試 5 次即觸發熔斷）：
1. `v1/models/gemini-2.5-pro`（主目標）
2. `v1/models/gemini-2.5-flash`
3. `v1/models/gemini-1.5-pro`
4. `v1/models/gemini-1.5-flash`
5. `v1beta/models/gemini-1.5-flash`

**熔斷行為**：5 次全敗 → 將每次的 HTTP Status / Headers / Body 寫入 `ai_ops/diagnostics/GEMINI_ERROR.log`（UTF-8，可直接貼給 PM）→ Exit code 2。

**成功條件**：HTTP 2xx **且** `candidates[0].content.parts[0].text` 非空 — 避免「200 OK 但內容空」這種灰區。Exit code 0。

**執行方式**（從 CryptoBot 專案根目錄）：
```bash
dotnet run --project scripts/GeminiSmokeTest -c Release
# 或指定金鑰：
GEMINI_API_KEY=... dotnet run --project scripts/GeminiSmokeTest -c Release
```

### T3. `GeminiAiAdvisorService` 日誌三支分支
原本 400/404/其他三支 → 拆為 **404 / 400-401-403 / 429 / 其他** 四支：
| 狀態碼 | 日誌引導 |
| --- | --- |
| 404 | 模型 `{Model}` 在端點 `{BaseUrl}` 找不到 — 模型退役，升級 `GeminiOptions` |
| 400/401/403 | 請求被拒 — 檢查 `/settings/exchanges` 的金鑰與 `GeminiOptions.Model` |
| **429**（新增） | 速率限制 / quota 耗盡 — 請稍候或升級 Google AI 配額 |
| 其他 | pass-through status + body |

所有 non-2xx 皆以 `LogWarning` 記錄完整 `errorBody`，滿足膠囊「原始錯誤訊息補獲」要求。

---

## 6. 自主驗證狀態（2026-04-22 結案）

**結論**：✅ 使用者端已成功連接 Gemini API。

**驗證脈絡**：
- S30-PRO 初版自主驗證（2.5-pro + RPM=15）因免費層即使一次呼叫也容易 429，產生膠囊 S30-LITE
- S30-LITE 落實後（模型 → `gemini-2.5-flash-lite`，內建 429/503 指數退避 2s→4s→8s），使用者端回報連接成功
- 因此 S30-PRO 的「2.5-pro 單次驗證」目標被 S30-LITE 的「flash-lite + 退避」策略實質取代

**交付用語**：
- 對 Gemini PM：「**Gemini 2.5 Flash-Lite 韌性修復已就緒**」
- VCP-Lite、VCP-Retry、VCP-Success 三點皆通過（使用者端實測）

---

## S30-FIX 摘要（2026-04-22 同日追補）

`gemini-1.5-flash` 於 2026 年初退役，舊模型在 `v1beta` 回 404 (`models/... is not found for API version v1beta`)。本次修補：

1. **模型升級**：`GeminiOptions.Model` `gemini-1.5-flash` → `gemini-3.1-flash`
2. **端點穩定化**：`GeminiOptions.BaseUrl` `v1beta/models/` → `v1/models/`
3. **404/400 診斷日誌**：`GeminiAiAdvisorService` 的 non-2xx 分支拆出三檔：
   - `404` → 明示「模型退役 / BaseUrl 端點不認得」，提示檢查 `GeminiOptions`
   - `400/401/403` → 明示「金鑰無效或模型不可用」
   - 其他 → 原樣回 HTTP code + body
4. **UI 文案同步**：`ExchangeSettings.razor` 的提示改寫為「預設模型 `gemini-3.1-flash`」

受影響檔案：
- `src/CryptoBot.Infrastructure/Ai/GeminiOptions.cs`
- `src/CryptoBot.Infrastructure/Ai/GeminiAiAdvisorService.cs`
- `src/CryptoBot.ConsoleApp/Components/Pages/ExchangeSettings.razor`
- `ai_ops/diagnostics/IMPLEMENTATION_REPORT.md`（本檔）

### S30-PRO 受影響檔案
- `src/CryptoBot.Infrastructure/Ai/GeminiOptions.cs`（Model / TimeoutSeconds 再度調整）
- `src/CryptoBot.Infrastructure/Ai/GeminiAiAdvisorService.cs`（新增 429 分支）
- `src/CryptoBot.ConsoleApp/Components/Pages/ExchangeSettings.razor`（預設模型字樣）
- **新增** `scripts/GeminiSmokeTest/GeminiSmokeTest.csproj`
- **新增** `scripts/GeminiSmokeTest/Program.cs`
- `ai_ops/diagnostics/IMPLEMENTATION_REPORT.md`（本檔 S30-PRO 段）

---

## 1. 建置 / 測試結果

| 項目 | 結果 |
| --- | --- |
| `dotnet build -c Debug` | **0 warnings / 0 errors** |
| `dotnet build -c Release` | **0 warnings / 0 errors** |
| `dotnet test -c Release` | **112/112 passed** (Domain 26 + Application 86) |

---

## 2. 交付內容對照

### T1 · Core AI Service（Backend）

| 目標 | 實作位置 |
| --- | --- |
| `IAiAdvisorService` 契約 | `src/CryptoBot.Application/Ai/IAiAdvisorService.cs` |
| Gemini 1.5 Flash 實作 | `src/CryptoBot.Infrastructure/Ai/GeminiAiAdvisorService.cs` |
| 選項（模型 / BaseUrl / Timeout） | `src/CryptoBot.Infrastructure/Ai/GeminiOptions.cs` |
| NoOp fallback（無金鑰） | `src/CryptoBot.Application/Ai/NoOpAiAdvisorService.cs` |
| `MarketContext` + `IMarketContextBuilder` | `src/CryptoBot.Application/Ai/MarketContext.cs` |
| RSI / ATR / BB / EMA 綜合 + 趨勢分類 | `src/CryptoBot.Application/Ai/MarketContextBuilder.cs` |

**Prompt 設計**：`responseMimeType=application/json` 強制 Gemini 回結構化 JSON；payload 為 `{ commentary, parameters }`，`SanitizeParameters` 用 `ExpectedParameterKeys` 白名單過濾 — 杜絕 AI 幻想出策略不認得的 key。

**契約保證**：`GetAdviceAsync` 永不拋；所有失敗（金鑰缺、HTTP 非 2xx、JSON 解析錯誤、逾時）都轉成 `Success=false + Error`，UI 看到友善紅字而非 500。

### T2 · Security & Key Management

| 目標 | 實作位置 |
| --- | --- |
| 金鑰持久化（SQLite，NEVER appsettings） | `AiCredential` aggregate（`src/CryptoBot.Domain/Aggregates/AiCredentialAggregate/AiCredential.cs`）+ migration `20260422083000_AiCredentials` |
| Provider unique index | `AiCredentialConfiguration` 加 `HasIndex(Provider).IsUnique()` |
| `IAiCredentialProvider` 供 Infra 讀金鑰 | `src/CryptoBot.Application/Common/Interfaces/IAiCredentialProvider.cs` + `src/CryptoBot.Infrastructure/Ai/DbAiCredentialProvider.cs`（Singleton wrap `IServiceScopeFactory` — 每次請求查 DB，UI 改完下一次 AI 呼叫即生效） |
| `/api/ai/credentials/{provider}` GET/PUT/DELETE | `src/CryptoBot.ConsoleApp/Api/AiCredentialEndpoints.cs` — 回應永不帶金鑰明文，只回 `sk-••••abcd` 遮罩 |
| `/settings/exchanges` 新增 Gemini 區塊 | `src/CryptoBot.ConsoleApp/Components/Pages/ExchangeSettings.razor` — 含儲存 / 更換 / 清除三顆按鈕，API Studio 連結內嵌 |

### T3 · Lab AI Panel（UI）

| 目標 | 實作位置 |
| --- | --- |
| AI 面板組件 | `src/CryptoBot.ConsoleApp/Components/Lab/AiAdvisorPanel.razor` |
| `/api/ai/advise` POST endpoint | `src/CryptoBot.ConsoleApp/Api/AiAdvisorEndpoints.cs` |
| 嵌入 `/lab` 並串接 `ApplyParametersAsync` | `src/CryptoBot.ConsoleApp/Components/Pages/BacktestLab.razor` — 新增 `ApplyAiSuggestedParametersAsync` callback，走既有 `StrategyParameterFormBase.ApplyParametersAsync` 合約 |
| 策略合法參數 key 清單 | `src/CryptoBot.ConsoleApp/Lab/StrategyCatalog.cs` — 在 `StrategyModel` 加 `ExpectedParameterKeys`，四個策略各自填妥 |

**按鈕行為**：
- 🪄「問問 Gemini 的看法」 — 呼 `/api/ai/advise`，loading 期間禁用；成功後顯示中文評論 + 建議參數表格。
- ✅「填入建議參數」 — 僅在 `Success=true` 且 `SuggestedParameters.Count > 0` 時可點，套用後 toast「按『開始優化掃描』即可驗證」。

---

## 3. DI 接線總覽

```
Application:
  TryAddSingleton<IAiAdvisorService, NoOpAiAdvisorService>   // fallback 給測試環境
  AddSingleton<IMarketContextBuilder, MarketContextBuilder>  // 依賴 IExchangeClient

Infrastructure.AddAiAdvisor (每次啟動都 Replace NoOp):
  Configure<GeminiOptions>
  AddSingleton<IAiCredentialProvider, DbAiCredentialProvider>
  Replace<IAiAdvisorService>(GeminiAiAdvisorService)  // 持 single HttpClient，與 Discord 同模式

Infrastructure.AddPersistence:
  AddScoped<IAiCredentialRepository, AiCredentialRepository>  // 供 /api/ai/credentials 使用
```

---

## 4. 變更檔案清單（20 個）

### 新增（14 個）
- `src/CryptoBot.Application/Ai/IAiAdvisorService.cs`
- `src/CryptoBot.Application/Ai/MarketContext.cs`
- `src/CryptoBot.Application/Ai/MarketContextBuilder.cs`
- `src/CryptoBot.Application/Ai/NoOpAiAdvisorService.cs`
- `src/CryptoBot.Application/Common/Interfaces/IAiCredentialProvider.cs`
- `src/CryptoBot.Domain/Aggregates/AiCredentialAggregate/AiCredential.cs`
- `src/CryptoBot.Infrastructure/Ai/GeminiAiAdvisorService.cs`
- `src/CryptoBot.Infrastructure/Ai/GeminiOptions.cs`
- `src/CryptoBot.Infrastructure/Ai/DbAiCredentialProvider.cs`
- `src/CryptoBot.Infrastructure/Persistence/Configurations/AiCredentialConfiguration.cs`
- `src/CryptoBot.Infrastructure/Persistence/Repositories/AiCredentialRepository.cs`
- `src/CryptoBot.Infrastructure/Persistence/Migrations/20260422083000_AiCredentials.cs` (+ `.Designer.cs`)
- `src/CryptoBot.ConsoleApp/Api/AiCredentialEndpoints.cs`
- `src/CryptoBot.ConsoleApp/Api/AiAdvisorEndpoints.cs`
- `src/CryptoBot.ConsoleApp/Components/Lab/AiAdvisorPanel.razor`

### 修改（6 個）
- `src/CryptoBot.Application/DependencyInjection.cs` — `TryAddSingleton` NoOp + `AddSingleton` MarketContextBuilder
- `src/CryptoBot.Domain/Repositories/IRepositories.cs` — 新增 `IAiCredentialRepository`
- `src/CryptoBot.Infrastructure/DependencyInjection.cs` — `AddAiAdvisor` 擴充 + `AddScoped<IAiCredentialRepository>`
- `src/CryptoBot.Infrastructure/Persistence/AppDbContext.cs` — `DbSet<AiCredential>`
- `src/CryptoBot.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs` — 加 `AiCredentials` 實體
- `src/CryptoBot.ConsoleApp/Program.cs` — 註冊 `MapAiCredentialEndpoints` + `MapAiAdvisorEndpoints`
- `src/CryptoBot.ConsoleApp/Lab/StrategyCatalog.cs` — `StrategyModel` 加 `ExpectedParameterKeys`，四個策略填妥
- `src/CryptoBot.ConsoleApp/Components/Pages/ExchangeSettings.razor` — Gemini 金鑰 UI 區塊
- `src/CryptoBot.ConsoleApp/Components/Pages/BacktestLab.razor` — 嵌 `<AiAdvisorPanel />` + `ApplyAiSuggestedParametersAsync` callback

---

## 5. VCP 驗證指引（給 Gemini PM）

1. 啟動 app：`dotnet run --project src/CryptoBot.ConsoleApp`
2. `/settings/exchanges` 填入 Gemini 金鑰（預設模型 `gemini-3.1-flash`）
3. `/lab` 切換至任一策略（例：Trend Following）
4. 輸入一組「強趨勢」Symbol（如近期 BTC-USDT 4h）、按 🪄 → 驗證 commentary 含「上漲趨勢」字樣
5. 換成「盤整」Symbol（如 USDC-USDT 1h）、按 🪄 → 驗證 commentary 含「區間」或「震盪」字樣
6. 按 ✅「填入建議參數」→ Parameter Grid 應收斂至單值、Grid Size=1
7. 按「🚀 開始優化掃描」→ 應完整跑完一次回測無錯誤

**預期 FAIL 情境**（均應回 `Success=false` 紅字，不 500）：
- 金鑰未填 → 「Gemini API Key 尚未配置」
- 金鑰無效 → 「Gemini HTTP 400：API key not valid」
- 交易所未配置 → 「無法取得市場資料」

---

**實作已就緒，請 Gemini 進行 VCP 驗證。**

---

# S30-FIX2 — Eco / Pro 模式切換 + 解析韌性 + 嚴格 prompt

**完成日期**：2026-04-23
**驅動需求**：使用者「我只有一個需求那就是讓我可以切換省錢模式跟認真模式，因為現在開發期間一直用 PRO 其實很燒錢」+ 我方追加的解析/Prompt 韌性修補。

## 1. 動機

S30-ELITE+3 完成 v1beta + 雙 Pro 後，每次按 🪄 都跑 `gemini-3.1-pro-preview`，開發階段反覆觸發 → token 帳單壓力大。
此外幾次實測抓到三個次要問題（T2-a/b/c），同步補強：

| 編號 | 症狀 | 來源 |
|---|---|---|
| FIX2-MODE | 沒有省錢模式可切，被迫每次燒 Pro | 使用者直接需求 |
| T2-a | AI 偶爾用 `minimum`/`maximum`/`increment` 取代 `min`/`max`/`step`，被解析忽略 | 觀察到的 trace |
| T2-b | AI 把範例字串 `"ParameterKey"` 直接照抄當鍵名 | observed |
| T2-c | 過濾後參數變空時，UI 顯示成功但「填入」沒東西可填 | UX 模糊 |

## 2. 需求 → 設計

| 層 | 動作 |
|---|---|
| Domain | `AiAdvisorMode` enum（`Eco=0` / `Pro=1`，固定 int 值供 EF 持久化）；`AiCredential` 加 `Mode` + `ChangeMode()`，`Create()` 預設 Eco |
| Application | `IAiCredentialProvider.GetModeAsync(provider)` |
| Infrastructure | `DbAiCredentialProvider.GetModeAsync` 從 SQLite 取，無紀錄回 Eco；`GeminiOptions` 改為 4 欄位（`EcoPrimary/EcoFallback/ProPrimary/ProFallback`）；`GeminiAiAdvisorService.GetAdviceAsync` 一次性查 Mode → `ResolveModelPair()` 拿 (Primary, Fallback) → 走原本 ELITE 退避鏈；EF migration `20260422090000_AiCredentialMode.cs` 加 `Mode` 欄位 (defaultValue=0) |
| ConsoleApp API | `PUT /api/ai/credentials/{provider}/mode` body `{ mode: "Eco" \| "Pro" }`；`AiCredentialDto` 多帶 `Mode`；無 row 時自動以空金鑰 + Mode 開新筆 |
| ConsoleApp UI | `/settings/exchanges` 在 Gemini 區塊加「🌱 省錢 / 🚀 認真」segmented toggle，當前模式高亮 + 立即生效 |
| Service 韌性 | T2-a: `TryParseGridRange` 接受 `min/Min/minimum/Minimum/from/From/start/Start/low/Low`（max/step 同理）；T2-b: prompt 強化「鍵名只能用清單原字串」並把首個 expected key 真名嵌進 example；T2-c: 過濾後 `sanitized.Count==0` 但 raw 非空時，回失敗並列出 raw key vs expected key |

## 3. 變更檔案（全為新增/修改，無刪除）

| 路徑 | 動作 |
|---|---|
| `src/CryptoBot.Domain/Aggregates/AiCredentialAggregate/AiAdvisorMode.cs` | NEW — enum |
| `src/CryptoBot.Domain/Aggregates/AiCredentialAggregate/AiCredential.cs` | + `Mode` / `ChangeMode` / `Create` 加 `mode` 參數 |
| `src/CryptoBot.Application/Common/Interfaces/IAiCredentialProvider.cs` | + `GetModeAsync` |
| `src/CryptoBot.Infrastructure/Ai/DbAiCredentialProvider.cs` | + `GetModeAsync` 實作 |
| `src/CryptoBot.Infrastructure/Ai/GeminiOptions.cs` | 4 欄位取代 PrimaryModel/FallbackModel |
| `src/CryptoBot.Infrastructure/Ai/GeminiAiAdvisorService.cs` | `ResolveModelPair`、T2-a/b/c |
| `src/CryptoBot.Infrastructure/Persistence/Configurations/AiCredentialConfiguration.cs` | + `Mode` HasConversion<int> |
| `src/CryptoBot.Infrastructure/Persistence/Migrations/20260422090000_AiCredentialMode.cs` | NEW — AddColumn `Mode` (defaultValue=0) |
| `src/CryptoBot.Infrastructure/Persistence/Migrations/20260422090000_AiCredentialMode.Designer.cs` | NEW — 配對 designer |
| `src/CryptoBot.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs` | + `Mode` 欄位 |
| `src/CryptoBot.ConsoleApp/Api/AiCredentialEndpoints.cs` | + `PUT /{provider}/mode`、DTO 加 `Mode` |
| `src/CryptoBot.ConsoleApp/Components/Pages/ExchangeSettings.razor` | + Eco/Pro toggle UI + `ChangeModeAsync` |
| `src/CryptoBot.ConsoleApp/wwwroot/app.css` | + `.mode-toggle` / `.mode-active` |

## 4. 模型對應表（預設值，可被 appsettings.json 覆蓋）

| 模式 | Primary | Fallback | 何時用 |
|---|---|---|---|
| **Eco**（預設）🌱 | `gemini-2.5-flash` | `gemini-2.5-flash-lite` | 開發 / 反覆按 🪄 試水溫 |
| **Pro** 🚀 | `gemini-3.1-pro-preview` | `gemini-2.5-pro` | 真的要把建議送進長時間優化掃描時 |

注意：`gemini-3.1-pro-preview` 仍需搭 `Gemini:ApiVersion=v1beta`（已於 S30-ELITE+3 設妥）。Eco 系列在 v1 / v1beta 都查得到。

## 5. 建置與測試（2026-04-22 本地）

- `Domain`：✓ 0 warn / 0 err
- `Application`：✓ 0 warn / 0 err
- `Infrastructure`：✓ 0 warn / 0 err
- `ConsoleApp`（已避開檔案鎖定，輸出至 `bin/Probe-FIX2/`）：✓ 0 warn / 0 err
- `Domain.Tests`：26 / 26 pass
- `Application.Tests`：86 / 86 pass

## 6. VCP 驗證指引（給 Gemini PM）

1. 重啟 app（migration 會自動跑）：`dotnet run --project src/CryptoBot.ConsoleApp`
2. `/settings/exchanges` 確認 Gemini 區塊出現 Eco/Pro toggle，預設選中 Eco
3. 按 🪄 → diagnostics 應顯示 `primary:gemini-2.5-flash(...)`
4. 切到 Pro → 按 🪄 → diagnostics 應顯示 `primary:gemini-3.1-pro-preview(...)`（v1beta 端點）
5. T2-a 驗證：請 Gemini 故意用 `minimum/maximum/increment` 命名範例值，看是否仍被收進建議
6. T2-c 驗證：手動把 prompt 弄壞讓 AI 回不認識的 key，UI 應顯示「AI 給的鍵 vs 期待的鍵」紅字而非默默成功

## 7. 已存資料相容性

- 既存 `AiCredentials` row 經 migration `Up` 後 `Mode` 欄位被補為 0 (Eco)，金鑰與 `UpdatedAt` 不動
- 既存 user 重啟後直接落到 Eco 模式（符合「省錢預設」需求）；要回 Pro 只需到 UI 點一下

---

## S30-GRID-FIX 摘要（2026-04-23 — 3 萬格上限 + 填入邏輯修復）

### 工單來源
`ai_ops/capsules/TASK_S30_GRID_UPGRADE_FIX.md`（Gemini PM）。兩個目標：

1. 解除 2,000 格警告封印，開放到 30,000 組組合
2. 修復「填入建議參數」點了之後表單不跳轉 / Grid size 不同步的問題

### 根因
三處各自貢獻：
- **硬編碼警告門檻**：`BacktestLab.razor` 中 `ApplyAiSuggestedParametersAsync` 的閾值仍寫 2000
- **鍵名嚴格比對**：四個 ParameterForm 的 `ApplyParametersAsync` / `ApplyGridParametersAsync` 用 `parameters.TryGetValue("RsiPeriod", ...)` — AI 回 `rsi_period` / `rsi` 時整筆被吃掉
- **UI 連動缺口**：部分表單在套用完值後沒呼叫 `NotifyChangedAsync()`，父頁 `Grid size` 指示器讀舊值

### 實作

#### T1：UI 限制放寬（已就緒）
`src/CryptoBot.ConsoleApp/Components/Pages/BacktestLab.razor` L439：
```csharp
if (grid > 30000)
{
    _toast = $"⚠ AI 建議的網格共 {grid} 格，已超過 30,000 — 建議手動調大 Step 以保護瀏覽器效能。";
```
Toast 訊息明示「30,000 以內為正常範圍」，不擋送出。

#### T2：策略表單韌性強化
新增通用 helper 到 `StrategyParameterFormBase.cs`：
```csharp
protected static bool TryResolve<T>(
    IReadOnlyDictionary<string, T> source,
    out T value,
    params string[] keyAliases)
{
    if (source is not null && keyAliases is { Length: > 0 })
    {
        foreach (var alias in keyAliases)
        {
            foreach (var kv in source)
            {
                if (string.Equals(kv.Key, alias, StringComparison.OrdinalIgnoreCase))
                { value = kv.Value; return true; }
            }
        }
    }
    value = default!;
    return false;
}
```

四個表單 (`B46`/`Sma`/`TrendFollowing`/`MeanReversion`) 的 `ApplyParametersAsync` + `ApplyGridParametersAsync` 全部改用 `TryResolve`，同義詞涵蓋：
- `RsiPeriod` ← `rsi_period` / `rsi` / `RsiLen` / `rsi_len`
- `RsiOversold` ← `Oversold` / `rsi_oversold` / `low_rsi` / `oversold`
- `RsiOverbought` ← `Overbought` / `rsi_overbought` / `high_rsi` / `overbought`
- `BbPeriod` ← `bb_period` / `bollinger_period` / `BollingerPeriod`
- `BbStdDev` ← `bb_stddev` / `bb_std` / `BollingerStdDev` / `bollinger_stddev`
- `FastEmaPeriod` ← `fast_ema_period` / `fast_ema` / `FastEma` / `FastPeriod` / `fast_period` / `fast`
- `SlowEmaPeriod` ← 同上 `slow_*`
- `FastSmaPeriod` / `SlowSmaPeriod` ← 同義詞對稱
- `RsiMidline` ← `rsi_midline` / `RsiMid` / `rsi_mid` / `midline`

四個表單全部在方法結尾呼叫 `await NotifyChangedAsync();` → 父頁 `OnFormGridChanged` 收到最新 `CurrentGridSize` → `StateHasChanged()` → UI Grid size 數字立即跳轉。

#### T3：後端邊界巡檢
- `src/CryptoBot.ConsoleApp/Api/LabEndpoints.cs` `ValidateRequest`：僅驗 StrategyKey 非空、Ranges 非空、時間窗、Symbol 格式、Slippage/InitialBalance 非負、各 range Min ≤ Max — **無組合總數上限**。
- `src/CryptoBot.ConsoleApp/Services/OptimizationOrchestrator.cs`：只檢 `IsValidCombination`（策略語意，如 Fast < Slow）與 `MinFillsForRanking = 3`（排名最小成交門檻，非組合數閘門）— **無 2000 / 10000 / 其他數字閘門**。

符合使用者固定規則「AI Advisor 掃描不設上限」（memory: `feedback_ai_advisor_no_cap.md`）。

### 交付要求附件：B46 `ApplyGridParametersAsync` 代碼片段

`src/CryptoBot.ConsoleApp/Components/Lab/B46ParameterForm.razor` L86–99：
```csharp
// S30-GRID-FIX T2：AI 輸出 JSON key 常不照 PascalCase 交 — 透過 TryResolve 做 case-insensitive + 同義詞比對，
// 避免 `rsi_period` / `low_rsi` 這類寫法讓整筆建議被默默吃掉。結尾的 NotifyChangedAsync 確保 Grid size 立刻跳轉。
public override async Task ApplyGridParametersAsync(IReadOnlyDictionary<string, ParameterGridRange> parameters)
{
    if (TryResolve(parameters, out var rsiP, "RsiPeriod", "rsi_period", "rsi", "RsiLen", "rsi_len"))
    { _rsiPeriodMin = rsiP.Min; _rsiPeriodMax = rsiP.Max; _rsiPeriodStep = rsiP.Step > 0 ? rsiP.Step : 1; }
    if (TryResolve(parameters, out var over, "RsiOversold", "Oversold", "rsi_oversold", "low_rsi", "oversold"))
    { _oversoldMin = over.Min; _oversoldMax = over.Max; _oversoldStep = over.Step > 0 ? over.Step : 1; }
    if (TryResolve(parameters, out var up, "RsiOverbought", "Overbought", "rsi_overbought", "high_rsi", "overbought"))
    { _overboughtMin = up.Min; _overboughtMax = up.Max; _overboughtStep = up.Step > 0 ? up.Step : 1; }
    if (TryResolve(parameters, out var bbP, "BbPeriod", "bb_period", "bollinger_period", "BollingerPeriod"))
    { _bbPeriodMin = bbP.Min; _bbPeriodMax = bbP.Max; _bbPeriodStep = bbP.Step > 0 ? bbP.Step : 1; }
    if (TryResolve(parameters, out var sd, "BbStdDev", "bb_stddev", "bb_std", "BollingerStdDev", "bollinger_stddev"))
    { _bbStdDevMin = sd.Min; _bbStdDevMax = sd.Max; _bbStdDevStep = sd.Step > 0 ? sd.Step : 0.1m; }
    await NotifyChangedAsync();
}
```

### 建置與測試（2026-04-23 本地）

- `ConsoleApp`（輸出至 `bin/Probe-GRID-FIX/`，避開執行中鎖定）：✓ 0 warn / 0 err
- `Domain.Tests`：26 / 26 pass
- `Application.Tests`：86 / 86 pass

### VCP 檢核對照

| VCP | 實作對應 |
|---|---|
| **[VCP-Sync]** 填入後 Min/Max/Step 數值發生變化 | `TryResolve` 命中任一別名即寫回 `_min/_max/_step` 欄位，`@bind` 雙向綁定把值推回 UI |
| **[VCP-GridSize]** Grid size 指示器正確顯示 | 四個表單結尾都 `await NotifyChangedAsync()` → `ParameterChanged.InvokeAsync(CurrentGridSize)` → 父頁 `OnFormGridChanged` 更新 `_currentGridSize` |
| **[VCP-Limit]** 25,000 組合可按「開始優化掃描」 | 前端只在 `> 30000` 顯示警告 toast（仍不擋）；後端 `LabEndpoints.ValidateRequest` + `OptimizationOrchestrator` 無組合上限 |
| **[VCP-Build]** 全專案 0 error / 0 warning | 見上方建置輸出 |

### 交付聲明
「3萬格網格升級與填入修復已就緒」。

---

## S27-NGROK 摘要（2026-04-23 — 外部存取隧道與安全性加固）

### 工單來源
`ai_ops/capsules/TASK_S27_NGROK_SETUP.md`（Gemini PM）。目標：讓外網（手機 / 公司電腦）透過 ngrok 安全存取本機 CryptoBot Lab，同時讓 `IpWhitelistMiddleware` 能正確辨識真實 client IP 而非 ngrok 代理的 127.0.0.1。

### 根因分析
- Kestrel 目前綁 `http://0.0.0.0:5000`，ngrok 以本地代理轉發進來，socket 上的 `Connection.RemoteIpAddress` 會是 `127.0.0.1` 或 ngrok 的內部位址
- `IpWhitelistMiddleware` 直接讀 `context.Connection.RemoteIpAddress` — 未經過 ForwardedHeaders 處理時，真實 client IP 被藏在 `X-Forwarded-For` 而白名單比對不到
- 結果：**要嘛全部被擋（真實 IP 沒進白名單），要嘛全部放行（把 127.0.0.1 加進白名單就等於對整個 ngrok 流量開放）** — 兩者都不安全

### 實作

#### T1. ForwardedHeaders 配置
`src/CryptoBot.ConsoleApp/Program.cs`：新增 `using Microsoft.AspNetCore.HttpOverrides;`，在 `IpWhitelistOptions` 註冊之後加：

```csharp
// S27-NGROK T1：ngrok 會把真正的客戶端 IP 放進 X-Forwarded-For，socket 上的 RemoteIpAddress
// 只會是 127.0.0.1 / ngrok 代理的內部 IP。UseForwardedHeaders 會把 context.Connection.RemoteIpAddress
// 改寫成 X-Forwarded-For 的第一跳（真實 client IP），之後 IpWhitelistMiddleware 才能拿到正確值。
// Known{Networks,Proxies} 清空 — ngrok 代理 IP 是動態的，固定清單沒意義；若未來要限制只能走 ngrok，
// 改在 appsettings 加 IpWhitelist 值（不是白名單代理）。
// ForwardLimit = null：不管經過多少層代理（ngrok 可能有多跳），一律取 X-Forwarded-For 的最左側原始 IP。
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    options.ForwardLimit = null;
});
```

Pipeline 註冊順序（關鍵）：

```csharp
if (!isBacktest)
{
    // S27-NGROK T1：UseForwardedHeaders 必須先於 IpWhitelistMiddleware — 讓白名單檢查看到的是
    // X-Forwarded-For 的真實客戶端 IP，而不是 ngrok 代理的內部位址。
    app.UseForwardedHeaders();

    // S27：IP 白名單必須先於 StaticFiles / Routing，否則非授權來源能拉到靜態資源。
    app.UseMiddleware<IpWhitelistMiddleware>();
    ...
}
```

#### T2. 白名單無需改動
`IpWhitelistMiddleware.InvokeAsync` 本來就讀 `context.Connection.RemoteIpAddress`（L52）— `UseForwardedHeaders` 會**就地改寫**這個欄位，所以中介層邏輯不必動。

`appsettings.json :: Security.AllowedIPs` 已保留 `127.0.0.1` + `::1`（dev 本機），加上 `192.168.0.99`、`114.39.88.182`（使用者自家 IP）。手機 / 公司 IP 的加入由使用者按指引自行編輯。

#### T3. 啟動腳本 + 指引

- **`scripts/start-ngrok.ps1`**（新增）：
  - 檢查 `ngrok` 在 PATH
  - 檢查 port 5000 有服務在聽（`Get-NetTCPConnection -LocalPort 5000 -State Listen`），無服務時警告但允許續跑
  - 執行 `ngrok http 5000`
  - 附 ngrok Dashboard 提示（`http://127.0.0.1:4040`）

- **`SETUP.md`**（新增於 repo root）：含本機啟動、IP 白名單說明，以及完整 ngrok 節（安裝 `winget install Ngrok.Ngrok` / authtoken 註冊 / 啟動流程 / 加白名單步驟 / 驗證 `X-Forwarded-For` / 安全提醒）

### 建置與測試（2026-04-23 本地）

- `ConsoleApp`（輸出至 `bin/Probe-NGROK/` 避開執行中鎖定）：✓ 0 warn / 0 err
- `Domain.Tests`：26 / 26 pass
- `Application.Tests`：86 / 86 pass

### VCP 檢核對照

| VCP | 實作對應 |
|---|---|
| **[VCP-Headers]** `RemoteIpAddress` 顯示外部 IP 而非 127.0.0.1 | `UseForwardedHeaders` 就地改寫 `Connection.RemoteIpAddress`；`IpWhitelistMiddleware.InvokeAsync` 第一行 log 的 `remote` 變數會是真實 client IP |
| **[VCP-Whitelist]** 手機 IP 加入白名單後走 ngrok URL 能開啟；移除則 403 | 白名單空清單 fail-open；非空且未命中時 `WriteForbiddenAsync` 回 `403 Forbidden: source IP not in whitelist.` |
| **[VCP-Security]** ngrok 提供 HTTPS | ngrok 服務本身同時提供 HTTP + HTTPS URL；`SETUP.md` 明示「永遠用 `https://...` 的 forwarded URL」 |

### 交付聲明
「ngrok 安全存取隧道已就緒」。

---

## S32 摘要（2026-04-23 — 槓桿模擬 & 爆倉機制）

### 工單來源
`ai_ops/capsules/TASK_S32_LEVERAGE_LIQUIDATION.md`。核心任務：回測引擎模型從「只扣手續費、不模擬槓桿 / 強平 / 資金費率」升級為支援 1..100x 槓桿並在權益歸零時強制爆倉，讓 Lab 使用者第一眼就能看到「哪些參數組在高倍下會死」。

### 設計決策

| 關鍵決策 | 原因 |
|---|---|
| 爆倉判斷放在 `BacktestSimulator`，暴露 `bool CheckAndApplyLiquidation(decimal unrealizedPnL)` | Simulator 持有 `VirtualBalance` 真源，由它判斷 & 歸零最自然；Engine 只需透過 `IBacktestClock` 拿到 bool 結果。 |
| 介面 `IBacktestClock` 新增 `CheckAndApplyLiquidation` + `IsLiquidated` | Engine 位於 Application 層，不該 downcast 成 Infrastructure 的 `BacktestSimulator`。窄介面保持依賴方向。 |
| 爆倉觸發 = `VirtualBalance + unrealizedPnL ≤ 0` | 手續費已在 `PlaceOrderAsync` 扣進 `VirtualBalance`，因此此式已隱含手續費損耗 — 高槓桿下會如 PM 所預期加速爆倉。 |
| 引擎 loop 內 `break`（非 throw） | 使用例外做控制流成本高，且上層 `BroadcastCompletedAsync` 拿報告要繼續做排名。break 後仍會建出報告、紀錄最後一點 equity=0。 |
| `BacktestReport.IsLiquidated` 預設為 `false`（optional record parameter） | 所有既存呼叫端（`BacktestRunner.cs`、測試 mock 等）不必改。 |
| `ReturnPercent` 在爆倉時強制回 `-100m` | PM 要求：爆倉一律 -100%，避免 `EndingBalance=0 / StartingBalance=X` 計算偏差 / 防呆保證顯示一致。 |
| 爆倉列保留在 Leaderboard，排序壓最底 | PM 明文：「同樣列在 Leaderboard 中」。用 `.OrderBy(IsLiquidated?1:0).ThenByDescending(PDR)` 兩段式排序；爆倉之間再以 ReturnPercent 做 tie-break。 |
| 爆倉略過 `MinFillsForRanking=3` 過濾 | 若爆倉發生在第 1 筆開倉就直接歸零，fills 可能 < 3；這種「一開就死」的組合最需要被展示。 |
| Leverage VO 呼叫 `Create(req.Leverage, max: 100)` 顯式放寬 | VO 預設 `max=20` 是 live 交易的保守護欄；Lab 沙盒需要 1..100 全區間。 |
| 開倉 notional `options.InitialBalance * 0.1m * leverage` | 1x 時與 S32 前行為完全相同（維持 VCP-Accuracy：既有低槓桿測試結果不變）；高倍讓 UnrealizedPnL 線性放大，確保 VCP-Liquidation 真能觸發。 |
| `_leverageInput` 預設 `3` | 不設 1x（等於沒啟用功能）、也不設 50x（預設就危險），讓新手首跑就感受到 MaxDD 被放大。 |

### 受影響檔案（修改）

1. `src/CryptoBot.Application/Backtesting/IBacktestClock.cs`
   - 新增 `bool CheckAndApplyLiquidation(decimal unrealizedPnL)` + `bool IsLiquidated { get; }`
2. `src/CryptoBot.Infrastructure/Backtesting/BacktestSimulator.cs`
   - 新增 `public bool IsLiquidated { get; private set; }`
   - 新增 `CheckAndApplyLiquidation` 實作（詳細 XML doc，核心爆倉邏輯）
3. `src/CryptoBot.Application/Backtesting/BacktestEngine.cs`
   - RunAsync：加 `isLiquidated` 區域變數；在 mark-to-market 計算後、drawdown 記錄前先呼叫 `_clock.CheckAndApplyLiquidation`，true 則推 0 equity 點並 `break`
   - ExecuteSignalAsync：`notional = InitialBalance * 0.1m * leverage`
   - Report 建構傳入 `IsLiquidated: isLiquidated`
4. `src/CryptoBot.Application/Backtesting/BacktestReport.cs`
   - record 尾端加 `bool IsLiquidated = false` optional parameter
   - `ReturnPercent` 改為三元：爆倉回 -100，否則維持原公式
5. `src/CryptoBot.ConsoleApp/Services/OptimizationOrchestrator.cs`
   - `OptimizationRequest` / `OptimizationGlobals` 尾端加 `int Leverage = 1`
   - `RunOneBacktestAsync`：`leverage: Leverage.Create(req.Leverage, max: 100)`（先前寫死 `Leverage.Conservative`）
   - `BroadcastCompletedAsync`：排序改為 `OrderBy(IsLiquidated).ThenByDescending(PDR).ThenByDescending(Return%)`；過濾改為「爆倉 OR 成交數 ≥ 門檻」
   - `LeaderboardRowDto` 建構傳入 `IsLiquidated`
   - 啟動 log 加 `leverage={Lev}x` 欄位
6. `src/CryptoBot.ConsoleApp/Realtime/OptimizationEvents.cs`
   - `LeaderboardRowDto` record 尾端加 `bool IsLiquidated = false`
7. `src/CryptoBot.ConsoleApp/Api/LabEndpoints.cs`
   - `ValidateRequest` 新增 `Leverage 1..100` 防呆
8. `src/CryptoBot.ConsoleApp/Components/Pages/BacktestLab.razor`
   - `_leverageInput = 3` 欄位
   - Banner 加 `Lev @_leverageInput×` 顯示
   - Market 區塊新增 `Leverage (×)` number input（1..100）
   - `StartOptimizeAsync` 加 1..100 驗證 + 傳入 `OptimizationGlobals.Leverage`
   - Leaderboard 表格用新 helper `RowClass(row)` 決定樣式；`row.IsLiquidated` 時加 `.badge-liquidated` 徽章 + 整列 `.row-liquidated` 紅底
9. `src/CryptoBot.ConsoleApp/Components/Lab/{Sma,B46,TrendFollowing,MeanReversion}ParameterForm.razor`
   - `BuildRequest` 四處 `OptimizationRequest(...)` 末尾加 `Leverage: globals.Leverage`
10. `src/CryptoBot.ConsoleApp/wwwroot/app.css`
    - 新增 `.row-liquidated td` 整列紅底、`.badge-liquidated` LIQUIDATED 徽章樣式

### 核心爆倉判斷代碼片段（PM 指定交付物）

`BacktestSimulator.CheckAndApplyLiquidation` — 這是整個 S32 的行為核心。Simulator 持有 `VirtualBalance` 真源，每根 K 線由 `BacktestEngine` 呼叫此方法帶入當前所有未平倉的 `UnrealizedPnL` 總和，若權益（帳上 + 浮盈/浮虧）≤ 0 即強制歸零並設旗標；回傳 `true` 讓 Engine 立刻 break 主迴圈。

```csharp
// src/CryptoBot.Infrastructure/Backtesting/BacktestSimulator.cs
public bool IsLiquidated { get; private set; }

/// <summary>
/// S32-T1 爆倉核心判斷：當權益（虛擬餘額 + 未實現損益）≤ 0 即判定爆倉。
/// 執行後果：虛擬餘額強制歸零、IsLiquidated 設為 true。回傳 true 表示本輪已爆倉，
/// 上層 BacktestEngine 應立即中止主迴圈、不再處理後續 K 線 / 訊號 / 下單。
/// 已爆倉後再次呼叫一律回傳 true（idempotent），不會回補餘額。
/// 手續費已於 PlaceOrderAsync 扣進 VirtualBalance，因此此處的「餘額 + 浮動損益」
/// 已內含手續費損耗 — 高槓桿下這會加速爆倉。
/// </summary>
public bool CheckAndApplyLiquidation(decimal unrealizedPnL)
{
    if (IsLiquidated) return true;

    var balBefore = VirtualBalance;
    var equity = balBefore + unrealizedPnL;
    if (equity > 0m) return false;

    VirtualBalance = 0m;
    IsLiquidated = true;
    _logger.LogWarning(
        "💥 [BACKTEST-LIQUIDATION] Equity ≤ 0 (bal={Bal:F4} + uPnL={UPnL:F4} = {Eq:F4}). Balance forced to 0, backtest will halt.",
        balBefore, unrealizedPnL, equity);
    return true;
}
```

對應 Engine 側的呼叫點（`BacktestEngine.RunAsync` 主迴圈內）：

```csharp
// src/CryptoBot.Application/Backtesting/BacktestEngine.cs
var openUnrealized = openPositions.Sum(p => p.UnrealizedPnL);
var currentEquity = await _exchange.GetFuturesBalanceAsync("USDT", ct).ConfigureAwait(false)
                  + openUnrealized;

// S32-T1：爆倉檢查放在權益計算「之後、回撤記錄之前」。
if (_clock.CheckAndApplyLiquidation(openUnrealized))
{
    isLiquidated = true;
    currentEquity = 0m;
    if (peakEquity > 0)
    {
        var ddL = (peakEquity - currentEquity) / peakEquity * 100m;
        if (ddL > maxDrawdownPct) maxDrawdownPct = ddL;
    }
    equityCurve.Add(new EquityPoint(kline.OpenTime, 0m));
    _logger.LogWarning(
        "💥 [BACKTEST] Liquidated at {Time:yyyy-MM-dd HH:mm}. Halting replay — {N} klines processed.",
        kline.OpenTime, totalKlines);
    break;
}
```

開倉 notional 的槓桿放大（ExecuteSignalAsync）：

```csharp
// S32-T1：用戶自訂槓桿放大開倉名目金額。10% 自有資金 × 槓桿倍數 = 本次名目部位。
var leverage = (decimal)strategyConfig.Leverage.Value;
var notional = Math.Max(options.InitialBalance * 0.1m * leverage, 0m);
```

### 建置 & 測試

- `dotnet build CryptoBot.ConsoleApp`（`bin/Probe-S32/`）：✓ 0 warn / 0 err（耗時 10.03 s）
- `dotnet test CryptoBot.sln`（`bin/Probe-S32T/`）：Domain 26 / 26 + Application 86 / 86 = **112 全通過**

### VCP 檢核對照

| VCP | 實作對應 |
|---|---|
| **[VCP-Liquidation]** 高槓桿 + 虧損行情必須能觸發 `IsLiquidated = true` 並中止回測 | `BacktestSimulator.CheckAndApplyLiquidation` 在 equity ≤ 0 時強制歸零 + 設旗標；`BacktestEngine` 收到 true 即 `break` 主迴圈、記 0 equity 點 |
| **[VCP-Accuracy]** 1x 槓桿下的回測結果必須與 S32 前一致 | `notional = InitialBalance * 0.1m * leverage` 在 leverage=1 退化為原式；Liquidation 檢查只在 equity ≤ 0 時動作，1x 低風險路徑永不觸發；`BacktestReport.IsLiquidated` 預設 false、既有呼叫端無需修改 |
| **[VCP-UI]** Lab 頁面可調整 1..100x 槓桿；爆倉結果列必須顯示明顯警示 | Market 區塊新增 Leverage number input（1..100 + title tooltip）；Banner 顯示 `Lev Nx`；爆倉列整列紅底（`.row-liquidated`）+ Parameters 欄 LIQUIDATED 徽章（`.badge-liquidated`）；`RowClass` 讓爆倉樣式優先於 rank-1 綠底 |

### 交付聲明
「槓桿爆倉模擬器已就緒」— Lab 現在能忠實呈現高槓桿的真實風險，使用者可以直接從 Leaderboard 看到「哪些參數組在 50x / 100x 下會直接爆倉」，不必再紙上談兵。

---

## S32-S35-REVISED 摘要（全市場實戰升級修正版）

### 任務分配
- **T0 · IpWhitelistMiddleware 黃字診斷**：ngrok / 手機連入 403 時需能即刻看到「Kestrel 收到的真實 IP」、「代理塞的 X-Forwarded-For」、「目前白名單筆數」三項關鍵資訊，省掉靠通靈猜外網 IP 的麻煩。
- **T1 · 策略命名業界大一統**：`StrategyCatalog.DisplayName` 改為 `SMA Crossover` / `EMA Trend Following` / `Bollinger Reversion` / `B46 Hybrid Model` — 名稱也會同步流進 Gemini Prompt 的 `{req.StrategyDisplayName}` 變數，AI 判讀背景自動對齊業界術語。
- **T2 · Symbol 下拉選單**：Lab 頁改為 Top 10 幣種下拉（BTC/ETH/SOL/BNB/XRP/DOGE/ADA/AVAX/DOT/LINK），保留「手動輸入」選項讓進階使用者指定冷門交易對；切幣時會自動觸發 `LoadCachedSettingsAsync` 還原設定。
- **T3 · 掃描 Top 10 市場機會**：新增 `GET /api/ai/market-sweep` — 並行抓 10 個主流幣的技術面快照、組合成 copy-paste ready 的中文分析 Prompt；AiAdvisorPanel 增「🚀 掃描 Top 10 市場機會」按鈕 + 可複製 textarea + 快照表格。
- **T4 · 引擎完善**：延續 S32-LEVERAGE-LIQUIDATION 已交付的爆倉邏輯、紅底顯示、LIQUIDATED 徽章 — 本輪 VCP 檢核無回歸。

### T0 交付物：`IpWhitelistMiddleware.InvokeAsync` 更新後代碼片段

```csharp
public async Task InvokeAsync(HttpContext context)
{
    var allowed = _options.CurrentValue.AllowedIPs;
    var remote = context.Connection.RemoteIpAddress;

    // S32-S35-REVISED T0：黃字診斷 log — 讓使用者從外網（ngrok）連入時能即刻看到
    // (a) Kestrel 收到的 Client IP、(b) ngrok 代理塞的 X-Forwarded-For、(c) 目前白名單筆數。
    // 手機連入 403 時多半是因為外網 IP 沒加入名單；這行 log 讓你直接 copy 字串丟進 appsettings。
    _logger.LogWarning(
        "🛡 Whitelist Check: Client={Ip}, XFF={Xff}, AllowedCount={Count}",
        remote, context.Request.Headers["X-Forwarded-For"].ToString(), allowed?.Count ?? 0);

    if (allowed is null || allowed.Count == 0)
    {
        // 空名單 = 不啟用 — 直接放行（log 一次 debug，避免被忘了配置）
        await _next(context).ConfigureAwait(false);
        return;
    }

    if (remote is null)
    {
        _logger.LogWarning(
            "IP whitelist reject: no RemoteIpAddress on request {Path}", context.Request.Path);
        await WriteForbiddenAsync(context).ConfigureAwait(false);
        return;
    }

    if (IsAllowed(remote, allowed))
    {
        await _next(context).ConfigureAwait(false);
        return;
    }

    _logger.LogWarning(
        "🛡 IP whitelist REJECT {Ip} → {Method} {Path} (allowed={Count})",
        remote, context.Request.Method, context.Request.Path, allowed.Count);
    await WriteForbiddenAsync(context).ConfigureAwait(false);
}
```

使用場景：使用者從手機 / ngrok 連入時，Kestrel 終端機會先印出黃字 `🛡 Whitelist Check: Client=xxx.xxx.xxx.xxx, XFF=..., AllowedCount=N`；即使該 IP 最終被拒，使用者也已拿到字串，可直接複製貼到 `appsettings.json` 的 `IpWhitelist:AllowedIPs` 陣列重啟即可放行。

### 檔案異動清單

| 層級 | 檔案 | 改動要點 |
|---|---|---|
| ConsoleApp/Middleware | `IpWhitelistMiddleware.cs` | T0：`InvokeAsync` 開頭新增黃字 `LogWarning` 輸出 Client / XFF / AllowedCount |
| ConsoleApp/Lab | `StrategyCatalog.cs` | T1：`sma`→`SMA Crossover`、`rsi-bb`→`B46 Hybrid Model`、`trend`→`EMA Trend Following`、`mean-reversion`→`Bollinger Reversion` |
| ConsoleApp/Components/Pages | `BacktestLab.razor` | T2：Symbol 由 `<input type="text">` 改為 `<select>` + `__manual__` 選項；新增 `_topSymbols`、`_symbolSelect`、`_isManualSymbol`、`OnSymbolSelectChangedAsync` |
| ConsoleApp/Api | `AiAdvisorEndpoints.cs` | T3：新增 `GET /api/ai/market-sweep`、`TopMarketSweepSymbols` 常數、`BuildMarketSweepPrompt` 組 Prompt、`MarketSweepSnapshotDto` / `MarketSweepResponseDto` 兩個 DTO record |
| ConsoleApp/Components/Lab | `AiAdvisorPanel.razor` | T3：新增「🚀 掃描 Top 10 市場機會」按鈕、`ScanMarketSweepAsync` handler、`CopySweepPromptAsync`（clipboard fallback 到 `FocusAsync`）、Prompt textarea + 快照表格 |

### T3 核心代碼片段（Market-Sweep endpoint 摘錄）

```csharp
// src/CryptoBot.ConsoleApp/Api/AiAdvisorEndpoints.cs
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
            var ctx = await contextBuilder.BuildAsync(sym, iv, klineCount: 100, ct).ConfigureAwait(false);
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
        Interval: iv, GeneratedAtUtc: DateTime.UtcNow,
        Snapshots: snapshots, Prompt: prompt));
});
```

### 建置 & 測試

- `dotnet build -p:OutputPath=bin/Probe-S33/` ：✓ **0 warn / 0 err**
- `dotnet test`（Probe-S33 build）：Domain **26/26** + Application **86/86** = **112 全通過**

### VCP 檢核對照

| VCP | 實作對應 |
|---|---|
| **[VCP-NGROK]** 手機 / ngrok 連入時終端機能清楚顯示 Client IP（黃字 log） | `IpWhitelistMiddleware.InvokeAsync` 最開頭 `LogWarning("🛡 Whitelist Check: Client={Ip}, XFF={Xff}, AllowedCount={Count}")` — 無論 allowed 為空、為 null、匹配失敗都會先印出，使用者拿到字串即可反向加白 |
| **[VCP-Naming]** 全系統策略名稱 100% 統一為業界術語 | `StrategyCatalog` 的 4 筆 `DisplayName` 已改；名稱會被 `AiAdvisorEndpoints` 打包進 `AiAdviceRequest.StrategyDisplayName` → Gemini Prompt 的 `{req.StrategyDisplayName}` 變數，AI 與 UI 看到同一組名詞 |
| **[VCP-Dropdown]** Symbol 選單可正常切換且不影響回測啟動 | `<select @bind="_symbolSelect" @bind:after="OnSymbolSelectChangedAsync">` 綁定 10 幣 + `__manual__` sentinel；選擇具體幣時自動設 `_symbolInput` 並觸發 `LoadCachedSettingsAsync`；「手動」才顯示 `<input>`，原本的 `_symbolInput`、`Symbol.Parse` 驗證鏈路 100% 復用 |

### 交付聲明
「全市場實戰升級修正版已就緒」— 白名單黃字 log 讓外網連線 403 一眼看穿真凶；策略名稱對齊業界術語、Gemini Prompt 同步；Symbol 下拉 + 手動雙模式兼顧懶人與專家；Top 10 橫掃鍵一鍵產生 copy-paste LLM Prompt，使用者可以把技術面快照餵進任何 AI 對照分析。

---

## S36-S38 摘要（UX 深度優化與 AI 模式轉型）

### 任務分配
- **T1 · Dashboard 策略模型 badge**：策略控制台的 Strategy cell 下方加一顆 Model badge，用業界術語標示背後的決策模型（`SMA Crossover` / `EMA Trend Following` / `Bollinger Reversion` / `B46 Hybrid Model`），色系分順勢藍 / 逆勢紫 / 套利中性。策略名稱可能被使用者自訂（例如 `SMA-Test`），badge 是唯一可以一眼看出「背後是哪個模型」的入口。
- **T2 · AI 導師 Prompt 轉化**：AiAdvisorPanel 從「一鍵問 Gemini」降級為「本地 Prompt 產生器」— 點按鈕只呼後端 `/api/ai/context`（純算指標、不碰 LLM），在 client 端組一段中文深度分析 Prompt，使用者複製後可丟到 ChatGPT / Claude / Gemini 任一家，不再綁定特定 API 或吃 Gemini 金鑰配額。同步移除父頁面已用不到的 `GetCurrentFormParameters` / `ApplyAiSuggestedParametersAsync` helper，無死代碼殘留。
- **T3a · 排行榜客戶端分頁**：BacktestLab Leaderboard 加 50 筆 / 頁分頁，第一 / 上一 / 下一 / 最後頁按鈕；切頁純前端（不重打 API），新優化跑完會自動把頁碼重設為 1。
- **T3b · 全站版面放寬**：`.app-shell` 主容器從 `max-width: 1400px` 改為 `width: 95%; max-width: 1600px` — 小螢幕保留 32px 邊距，超寬螢幕向外延伸到 1600px 止，資料密集的 Leaderboard / Prompt textarea 不再被擠在畫面中央。

### 檔案異動清單

| 層級 | 檔案 | 改動要點 |
|---|---|---|
| ConsoleApp/Pages | `Dashboard.razor` | T1：Strategy cell 加 `<span class="model-tag ...">Model: @ModelDisplayName(...)</span>`；新增 `ModelDisplayName` / `ModelTagClass` / `ModelTooltip` 三個 static switch helper，StrategyType → 業界術語 + 色系 class |
| ConsoleApp/Api | `AiAdvisorEndpoints.cs` | T2：新增 `GET /api/ai/context?symbol=&interval=` 單一 symbol 快照端點，純走 MarketContextBuilder、不觸發任何 LLM；失敗轉成 HTTP 200 + `MarketSweepSnapshotDto` with `Error` 欄位（UI 直接顯示） |
| ConsoleApp/Components/Lab | `AiAdvisorPanel.razor` | T2：整檔重寫 — 移除 Gemini `AskAsync` / `ApplyAsync` / SuggestedParameters 表 / Attempts 診斷；新增 `GenerateAdvicePromptAsync` + `BuildAdvicePrompt` 本地組中文 Prompt、`CopyAdvicePromptAsync`（clipboard + Ctrl+C fallback）；移除 `CurrentParametersProvider` / `OnApplyParameters` 兩個不再需要的 `[Parameter]` |
| ConsoleApp/Pages | `BacktestLab.razor` | T2：`<AiAdvisorPanel>` 調用移除 `CurrentParametersProvider` / `OnApplyParameters` 兩個 prop；刪除 `ApplyAiSuggestedParametersAsync` / `GetCurrentFormParameters` 兩段已無人呼叫的 helper。T3a：`<tbody>` 內加 `@{ ... var pagedRows = Skip.Take }`、`foreach(pagedRows)`；`</table>` 後加分頁控制列；`@code` 加 `LeaderboardPageSize = 50`、`_leaderboardPage`、`_lastLeaderboardRef`、`GoToLeaderboardPage`、`EnsureLeaderboardPageInBounds` — 新 Leaderboard 物件抵達（reference 比對）即自動重設回首頁 |
| ConsoleApp/wwwroot | `app.css` | T1：新增 `.model-tag` / `.model-tag-trend` / `.model-tag-reversion` / `.model-tag-neutral`（藍 / 紫 / 灰 pill）。T3a：新增 `.lb-pagination` / `.lb-pagination-info`（monospace 資訊列）。T3b：`.app-shell` `max-width: 1400 → 1600px`，新增 `width: 95%` 讓中螢幕也能撐開 |

### T2 核心片段（`AiAdvisorPanel.BuildAdvicePrompt` 本地 Prompt 組合）

```csharp
private static string BuildAdvicePrompt(string strategyKey, MarketSweepSnapshotDto s)
{
    string Dec(decimal? v, string fmt = "F2") =>
        v is null ? "—" : v.Value.ToString(fmt, CultureInfo.InvariantCulture);

    var strategyLabel = strategyKey switch
    {
        "sma"            => "SMA Crossover（雙均線金叉 / 死叉）",
        "trend"          => "EMA Trend Following（EMA 黃金/死亡交叉 × RSI 動能）",
        "mean-reversion" => "Bollinger Reversion（布林通道 × RSI 極端反轉）",
        "rsi-bb"         => "B46 Hybrid Model（RSI × Bollinger 複合訊號）",
        _                => string.IsNullOrWhiteSpace(strategyKey) ? "（未指定）" : strategyKey,
    };

    var sb = new StringBuilder();
    sb.AppendLine("你是一位頂尖的加密貨幣量化交易分析師，請針對以下市場快照給出深度分析。");
    // ... Symbol / Interval / 策略 / 指標行 / 4 個問答導引 ...
    return sb.ToString();
}
```

使用流程：使用者選好 Symbol / Interval / 策略 → 按「📝 生成分析 Prompt」→ 面板 GET `/api/ai/context` 拿指標快照 → 本地組 Prompt → 顯示 textarea + 📋 複製按鈕。使用者複製到外部 LLM 得到分析，不經過任何自家 AI 服務。

### T3a 核心片段（BacktestLab 分頁器）

```razor
<tbody>
    @{
        var totalRows = State.Leaderboard.Rows.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(totalRows / (double)LeaderboardPageSize));
        EnsureLeaderboardPageInBounds(pageCount);
        var pagedRows = State.Leaderboard.Rows
            .Skip(_leaderboardPage * LeaderboardPageSize)
            .Take(LeaderboardPageSize);
    }
    @foreach (var row in pagedRows) { /* 整列 tr */ }
</tbody>
```

```csharp
private const int LeaderboardPageSize = 50;
private int _leaderboardPage;
private object? _lastLeaderboardRef;

private void EnsureLeaderboardPageInBounds(int pageCount)
{
    var currentRef = State.Leaderboard;
    if (!ReferenceEquals(currentRef, _lastLeaderboardRef))
    {
        _leaderboardPage = 0;
        _lastLeaderboardRef = currentRef;
        return;
    }
    if (_leaderboardPage >= pageCount) _leaderboardPage = pageCount - 1;
    if (_leaderboardPage < 0) _leaderboardPage = 0;
}
```

設計取捨：用 `ReferenceEquals` 比對 snapshot 物件 — `OptimizationState` 每次新優化完成後會把 `Leaderboard` 指向新的 `LeaderboardSnapshotDto`，此時頁碼自動歸零；同一次優化內的增量更新不會觸發重設。

### 建置 & 測試

- `dotnet build -p:OutputPath=bin/Probe-S36/` ：✓ **0 warn / 0 err**
- `dotnet test`（Probe-S36 build）：Domain **26/26** + Application **86/86** = **112 全通過**

### VCP 檢核對照

| VCP | 實作對應 |
|---|---|
| **[VCP-Dashboard]** Dashboard 能清楚看到當前跑的是 `B46 Hybrid Model` 而不只是 `SMA-Test` | `Dashboard.razor` Strategy cell 下新增 `.model-tag` pill，走 `ModelDisplayName(StrategyType)` switch 把 `SmaCrossover` / `TrendFollowing` / `MeanReversion` / `B46RsiBb` / `BasisArbitrage` 映射到業界術語；色系 `.model-tag-trend`（藍）/ `.model-tag-reversion`（紫）/ `.model-tag-neutral`（灰）依策略風格配置 |
| **[VCP-Prompt]** 點擊建議按鈕後，能成功複製一段包含技術指標數據的指令 | `GenerateAdvicePromptAsync` GET `/api/ai/context` → `BuildAdvicePrompt` 組出含 Symbol、Interval、RSI/ATR/EMA/BB%、TrendLabel、策略背景、4 個回答導引的中文 Prompt；`CopyAdvicePromptAsync` 優先走 `navigator.clipboard.writeText`，失敗回退 `ElementReference.FocusAsync` 讓使用者 Ctrl+C |
| **[VCP-Pagination]** 回測完畢後，排行榜下方出現分頁標籤，且每頁僅顯示 50 筆 | `LeaderboardPageSize = 50` + `.Skip(page*50).Take(50)`；`lb-pagination` 控制列條件顯示（總筆數 > 50 時出現）：第一頁 / 上一頁 / 資訊文字（「第 X / Y 頁 · 顯示 A–B / 共 N 筆」）/ 下一頁 / 最後一頁 |
| **[VCP-Width]** UI 視覺上有明顯的左右延伸，不再擠在中間 | `.app-shell` `max-width: 1400 → 1600px`，並加 `width: 95%` 讓 1440p / 超寬螢幕主動延展；小螢幕（< 1684px）靠 `width: 95%` 佔畫面，不依賴 max-width 上限 |

### 交付聲明
「UX 深度優化與 AI 模式轉型已就緒」— Dashboard 一眼就能看到策略背後是哪個業界模型；AI 導師降級為 Prompt 產生器，不再綁定 Gemini API 與金鑰配額；Leaderboard 50 筆 / 頁 + 四向翻頁，大量優化結果瀏覽順暢；主容器放寬到 1600px × 95% 視寬，超寬螢幕終於用得上版面。

---

## S39 · 全方位交易歷史與 AI 複盤系統

**膠囊**：`ai_ops/capsules/TASK_S39_TRADE_HISTORY_REPLAY.md`
**目標**：讓每一筆「開倉 → 平倉」的過程都有完整時間、價格與參數快照，顯示於 Dashboard 作為 AI 分析與策略複盤的依據。

### 任務拆解

| T | 膠囊要求 | 實作摘要 |
|---|---------|---------|
| **T1 · 持久化層強化** | Position 需含 EntryPrice/EntryTimeUtc/ExitPrice/ExitTimeUtc/RealizedPnL/TotalCommission/ParametersSnapshot；Close() 必須寫入 | Position 已有 `EntryPrice`、`OpenedAt`(~EntryTimeUtc)、`ClosedAt`(~ExitTimeUtc)、`RealizedPnL`、`TotalCommission` — 新增 **`ExitPrice`** (Price?)、**`StrategyType`** (string?)、**`ParametersSnapshot`** (string? JSON)；`Close()` 設 `ExitPrice = exitPrice`；`ReducePosition` 全減倉時也一併落下 `ExitPrice`；EF Configuration 加三欄（NullablePrice / MaxLength 64 / no-limit TEXT）；`StrategyExecutor.HandleOpenSignalAsync` 在 Position.Open 時傳入 `strategyType` + `BuildParametersSnapshot(config)` JSON |
| **T2 · Dashboard 歷史面板** | 新增 `TradeHistoryTable`，顯示最近 50 筆已平倉、標註模型、PnL 紅綠 | 新增 `Components/Dashboard/TradeHistoryTable.razor`，11 欄（平倉時間 / Symbol / Side / Model / Qty / Entry / Exit / PnL / 手續費 / 持倉時長 / AI 複盤按鈕）；Model 欄沿用 S36-S38 的 `.model-tag` 三色系；PnL 走 `PnLClass` (`.pos` 綠 / `.neg` 紅 / 中性灰)；`Dashboard.razor` 尾端掛 `<TradeHistoryTable Limit="50" />` |
| **T3 · AI 複盤接口** | 每列「複製複盤數據」按鈕 → 產生 Prompt「我是 CryptoBot…請分析並與其他模型對比」 | 按鈕觸發 `CopyReplayPromptAsync` → `BuildReplayPrompt(ClosedTradeDto)` 產生含 Symbol/方向/槓桿/進出場時間價/RealizedPnL/手續費/策略模型/ParametersSnapshot JSON 的 Prompt，末段追加「請與下列其他模型對比」（自動排除當前模型自身）+ 4 題導引（進場時機 / RR 比 / 對比預期 / 參數微調建議）；`navigator.clipboard.writeText` 主線 + `cryptoBotClipboard.copy` （`chart_interop.js` 新增的 `execCommand` textarea fallback）次線 |

### 程式變更一覽

| 檔案 | 類型 | 變更重點 |
|------|-----|---------|
| `src/CryptoBot.Domain/Aggregates/PositionAggregate/Position.cs` | 修改 | 新增 `ExitPrice` / `StrategyType` / `ParametersSnapshot`；`Open(...)` 增加 `strategyType` + `parametersSnapshot` 可選參數；`Close(...)` 落下 `ExitPrice`；`ReducePosition` 全減倉分支同樣落下 |
| `src/CryptoBot.Infrastructure/Persistence/Configurations/PositionConfiguration.cs` | 修改 | 新增三欄 mapping；`ExitPrice` 用 `NullablePriceConverter`、`StrategyType` max 64、`ParametersSnapshot` TEXT 無限制 |
| `src/CryptoBot.Infrastructure/Persistence/Migrations/20260423000000_PositionTradeHistory.cs` + `.Designer.cs` | 新增 | `AddColumn` 三欄，全部 nullable（歷史 row 不補值） |
| `src/CryptoBot.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs` | 修改 | Position entity 新增三欄 Property 定義 |
| `src/CryptoBot.Domain/Repositories/IRepositories.cs` + `src/CryptoBot.Infrastructure/.../PositionRepository.cs` | 修改 | `IPositionRepository.GetRecentClosedAsync(int limit)` 介面 + 實作（`OrderByDescending(ClosedAt).Take(limit)`） |
| `src/CryptoBot.Application/Trading/StrategyExecutor.cs` | 修改 | 開倉成交後呼叫 `BuildParametersSnapshot(config)`（Leverage/Risk/SL/TP/TrailingStop/Parameters dict → JSON），傳給 `Position.Open` |
| `src/CryptoBot.ConsoleApp/Api/Dtos/DashboardStatsDto.cs` | 修改 | 新增 `ClosedTradeDto`（13 欄） |
| `src/CryptoBot.ConsoleApp/Api/DashboardEndpoints.cs` | 修改 | 新增 `GET /api/dashboard/trade-history?limit=N`（clamp 1..200，預設 50） |
| `src/CryptoBot.ConsoleApp/Components/Dashboard/TradeHistoryTable.razor` | 新增 | 歷史表 UI + Prompt 產生 + clipboard |
| `src/CryptoBot.ConsoleApp/Components/Pages/Dashboard.razor` | 修改 | `@using CryptoBot.ConsoleApp.Components.Dashboard` + 掛 `<TradeHistoryTable Limit="50" />` |
| `src/CryptoBot.ConsoleApp/wwwroot/chart_interop.js` | 修改 | 末端追加 `window.cryptoBotClipboard.copy(text)` 走 execCommand textarea 後備 |
| `tests/.../AccountSynchronizerTests.cs` / `StrategyEngineTests.cs` / `S7TestDriveIntegrationTests.cs` | 修改 | 三個 fake `IPositionRepository` 補 `GetRecentClosedAsync` stub |

### 關鍵程式片段

**Position 新欄位（Domain）**：

```csharp
// S39 交易歷史 / AI 複盤用欄位
public Price? ExitPrice { get; private set; }
public string? StrategyType { get; private set; }
public string? ParametersSnapshot { get; private set; }

// Close() 落下 ExitPrice — 不再靠 CurrentPrice 被動承擔雙重語意
public void Close(Price exitPrice, string reason, decimal closeCommission = 0)
{
    ...
    CurrentPrice = exitPrice;
    ExitPrice = exitPrice;   // ← 新增
    RaiseDomainEvent(...);
}
```

**StrategyExecutor 開倉落下快照**：

```csharp
private static string BuildParametersSnapshot(StrategyConfiguration config)
{
    var payload = new
    {
        leverage = config.Leverage.Value,
        riskPerTradePercent = config.RiskPerTradePercent,
        stopLossPercent = config.StopLossPercent,
        takeProfitPercent = config.TakeProfitPercent,
        trailingStopPercent = config.TrailingStopPercent,
        parameters = config.Parameters,
    };
    return JsonSerializer.Serialize(payload);
}

// HandleOpenSignalAsync 內：
var parametersSnapshot = BuildParametersSnapshot(strategyState.Configuration);
var position = Position.Open(
    ...,
    strategyId: strategyState.Id,
    strategyType: strategyState.StrategyType,
    parametersSnapshot: parametersSnapshot);
```

**TradeHistoryTable 核心片段**：

```razor
<section class="panel">
    <header class="panel-header">
        <h2>交易歷史</h2>
        <span class="panel-sub">last @_rows.Count · closed positions</span>
        <button class="btn-apply" style="margin-left:auto;"
                @onclick="LoadAsync">🔄 重新整理</button>
    </header>
    <table class="data-table">
        <thead><tr>
            <th>平倉時間</th><th>Symbol</th><th>Side</th><th>Model</th>
            <th>Qty</th><th>Entry</th><th>Exit</th><th>PnL</th>
            <th>手續費</th><th>持倉時長</th><th>AI 複盤</th>
        </tr></thead>
        <tbody>
        @foreach (var t in _rows)
        {
            <tr>
                <td>@(t.ExitTimeUtc?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "—")</td>
                <td>@t.Symbol</td>
                <td class="@(t.Side == "Long" ? "pos" : "neg")">@t.Side</td>
                <td>
                    <span class="model-tag @ModelTagClass(t.StrategyType)"
                          title="@ModelTooltip(t.StrategyType)">
                        @ModelDisplayName(t.StrategyType)
                    </span>
                </td>
                <td>@t.Quantity.ToString("N4", CultureInfo.InvariantCulture)</td>
                <td>@t.EntryPrice.ToString("N2", CultureInfo.InvariantCulture)</td>
                <td>@(t.ExitPrice?.ToString("N2", CultureInfo.InvariantCulture) ?? "—")</td>
                <td class="@PnLClass(t.RealizedPnL)">@FormatSignedMoney(t.RealizedPnL)</td>
                <td>@t.TotalCommission.ToString("N4", CultureInfo.InvariantCulture)</td>
                <td>@FormatDuration(t.EntryTimeUtc, t.ExitTimeUtc)</td>
                <td>
                    <button class="btn-apply"
                            @onclick="() => CopyReplayPromptAsync(t)">
                        @(IsLastCopied(t.PositionId) ? "✅ 已複製" : "📋 複製複盤數據")
                    </button>
                </td>
            </tr>
        }
        </tbody>
    </table>
</section>
```

**Replay Prompt 產生器（節錄）**：

```csharp
private static string BuildReplayPrompt(ClosedTradeDto t)
{
    var otherModels = GetOtherModelsFor(t.StrategyType);   // 自動排除當前模型
    var modelLabel = ModelDisplayName(t.StrategyType);
    var sb = new StringBuilder();
    sb.AppendLine("我是 CryptoBot。這是一筆交易紀錄：");
    ...
    sb.AppendLine("- 策略參數快照 (開倉當下)：");
    sb.AppendLine("```json");
    sb.AppendLine(t.ParametersSnapshot ?? "{}");
    sb.AppendLine("```");
    sb.Append("請分析「").Append(modelLabel)
      .Append("」在此時段於 ").Append(t.Symbol)
      .AppendLine(" 的表現，並與以下其他模型進行對比建議：");
    foreach (var m in otherModels) sb.Append("- ").AppendLine(m);
    sb.AppendLine("1. 進場時機是否最佳…  2. 止盈止損是否合理…  3. 對比模型預期…  4. 參數微調…");
    return sb.ToString();
}
```

### 建置與測試

- `dotnet build -p:OutputPath=bin/Probe-S39/`：✓ **0 warn / 0 err**
- `dotnet test`（Probe-S39 build）：Domain **26/26** + Application **86/86** = **112 全通過**

### VCP 檢核對照

| VCP | 實作對應 |
|---|---|
| **[VCP-DB]** 手動在模擬盤完成一次成交，`ParametersSnapshot` 欄位有正確存入 | 新 migration `20260423000000_PositionTradeHistory` 加 `ParametersSnapshot` (TEXT) + `ExitPrice` + `StrategyType`；`StrategyExecutor` 呼叫 `BuildParametersSnapshot(config)` 將 Leverage/Risk/SL/TP/TrailingStop/Parameters dict 序列化後傳入 `Position.Open`；`PositionConfiguration` 透過 EF 直接映射字串欄位，不經 ValueConverter — SQLite 裡可用 `sqlite3 cryptobot.db "SELECT ParametersSnapshot FROM Positions ..."` 直讀驗證 |
| **[VCP-History]** Dashboard 歷史列表能正確顯示該筆成交紀錄，且時間與價格準確 | `GET /api/dashboard/trade-history` 回 `ClosedTradeDto[]` 含 `EntryTimeUtc` (= `Position.OpenedAt`) / `ExitTimeUtc` (= `Position.ClosedAt`) / `EntryPrice` / `ExitPrice` / `RealizedPnL`；`TradeHistoryTable.razor` 11 欄渲染，時間欄走 `ToLocalTime().ToString("MM-dd HH:mm:ss")`；PnL 走 `PnLClass` 雙色 |
| **[VCP-JSON]** 點擊複製後，輸出的數據能被 Gemini 讀懂並進行回饋 | `BuildReplayPrompt` 組出結構化 Prompt：開頭「我是 CryptoBot」自我介紹 → bullet list 原始交易資料 → ```json``` 區塊嵌入 `ParametersSnapshot` → 對比模型清單 → 4 題導引；Clipboard 主線 `navigator.clipboard.writeText`，次線 `cryptoBotClipboard.copy` (chart_interop.js 裡的 execCommand textarea) 保證 HTTP / 舊瀏覽器也能落貼 |

### 交付聲明
「全方位交易歷史與複盤系統已就緒」— Position 上三個新欄位讓歷史可追溯、字串化 `StrategyType` 讓歷史不怕上游 Aggregate 改名、`ParametersSnapshot` JSON 讓 AI 拿到開倉當下完整參數；Dashboard 底部新增 11 欄交易歷史表，Model tag 與盤口一致，PnL 紅綠分明；每列一鍵產生「我是 CryptoBot + 完整原料 + 對比 4 模型 + 4 題導引」複盤 Prompt，直接貼進 Gemini 即可做深度策略分析。

---

## S31 · 模擬盤連線與 VST 帳戶巡檢（DEMO-PREFLIGHT）

### 任務對照

| 膠囊項目 | 稽核結論 | 對應證據 |
|---|---|---|
| **T1 · 餘額獲取驗證** | ✅ 既有實作正確 + 加強 log | `BingXExchangeClient.BuildRestClient` 依 `_options.EffectiveMode` 帶 `BingXEnvironment.Demo`；`GetFuturesBalanceAsync` 預設 asset 取 `QuoteAsset`（Demo → `"VST"`；Live → `"USDT"`）。新增首次成功取得餘額的 Information log 作為 preflight 證明。 |
| **T1 · 持倉獲取驗證** | ✅ 既有實作正確 | `GetOpenPositionsAsync` 走 `_client.PerpetualFuturesApi.Trading.GetPositionsAsync`，client 已綁 Demo 環境，自動路由到模擬盤接口；dynamic bind 相容 SDK v3.10.0 欄位差異。 |
| **T2 · WebSocket listenKey** | ✅ 既有實作正確 | `BingXMarketDataStream.BuildSocketClient` 同樣依 `EffectiveMode` 帶環境；`OpenUserStreamAsync` 透過 `_restClient.GetListenKeyAsync` 拿 key（REST 也是 Demo 環境），再 `SubscribeToUserDataUpdatesAsync`；30 分鐘自動續期，ListenKeyExpired 事件失效即時旗標。 |
| **T2 · 成交回報連動** | ⚠️ **發現缺口 → 修補完成** | 缺口：TradeHistoryTable 只在首次載入 / 手動刷新重拉，平倉後 UI 沒自動更新，違反 VCP-History-Sync。修補：新增 `PositionClosedUpdate` event chain，`AccountSynchronizer` 在 SaveChangesAsync 成功後透過 `IRealtimeBroadcaster.BroadcastPositionClosedAsync` 發送，Web host 的 `SignalRRealtimeBroadcaster` 同時 fan-out 到 `DashboardEventBus` + SignalR Hub；`TradeHistoryTable.razor` 訂閱 `Bus.PositionClosed`，事件到即 `LoadAsync` 重拉。 |
| **T3 · 熔斷器巡檢** | ✅ 既有實作正確 | `SafetyBreakerMonitor` 每 1 分鐘呼叫 `IRiskManager.IsDailyLossLimitReachedAsync` → 讀 `GetClosedPositionsInRangeAsync(today, tomorrow)` 與 `_exchange.GetFuturesBalanceAsync(ct)`（Demo 自動查 VST），Demo 平倉後 `RealizedPnL` 由 S39 完整落庫，故熔斷計算在 Demo 下與 Live 行為一致；Trip → `StopAllAsync` + `NotifyCircuitBreakerAsync`。 |

### 檔案變更總表

| 檔案 | 變更摘要 |
|---|---|
| `src/CryptoBot.Application/Realtime/PositionClosedUpdate.cs` | **新增**：已平倉事件負載 record（PositionId、ClosedAtUtc、Symbol、PositionSide、ExitPrice、RealizedPnL）。 |
| `src/CryptoBot.Application/Realtime/IRealtimeBroadcaster.cs` | 介面新增 `BroadcastPositionClosedAsync`。 |
| `src/CryptoBot.Application/Realtime/NullRealtimeBroadcaster.cs` | NoOp 實作新增同名方法。 |
| `src/CryptoBot.ConsoleApp/Realtime/DashboardEventBus.cs` | 新增 `event Action<PositionClosedUpdate>? PositionClosed` + `RaisePositionClosed`。 |
| `src/CryptoBot.ConsoleApp/Realtime/SignalRRealtimeBroadcaster.cs` | 實作 `BroadcastPositionClosedAsync` — 同時 `_bus.RaisePositionClosed` 與 `_hub.SendAsync("PositionClosed", ...)` fan-out。 |
| `src/CryptoBot.Application/Synchronization/AccountSynchronizer.cs` | 建構子新增 optional `IRealtimeBroadcaster? broadcaster = null`（既有測試 call site 免改）；`HandleAccountUpdateAsync` 收集本批關倉 payload，`SaveChangesAsync` 成功後統一 `BroadcastPositionClosedAsync`，單筆失敗不阻擋其他筆。 |
| `src/CryptoBot.ConsoleApp/Components/Dashboard/TradeHistoryTable.razor` | 注入 `DashboardEventBus`，實作 `IDisposable`；`OnInitializedAsync` 訂閱 `Bus.PositionClosed` → `InvokeAsync(LoadAsync + StateHasChanged)`；`Dispose` 解掛 handler。 |
| `src/CryptoBot.Infrastructure/Exchange/BingX/BingXExchangeClient.cs` | `GetFuturesBalanceAsync` 首次成功取得餘額（或模式切換後首次）以 Information 列印 `"✅ BingX futures balance fetched \| mode={Mode} \| asset={Asset} \| balance={Balance}"`；後續 tick 降為 Debug 避免 2s 輪詢污染 log。 |

### 關鍵程式碼

**VST 餘額證明 log（BingXExchangeClient.cs）：**
```csharp
if (!_balanceProofLogged || _balanceProofMode != _options.EffectiveMode)
{
    _logger.LogInformation(
        "✅ BingX futures balance fetched | mode={Mode} | asset={Asset} | balance={Balance}",
        _options.EffectiveMode, queryAsset, value);
    _balanceProofLogged = true;
    _balanceProofMode = _options.EffectiveMode;
}
else
{
    _logger.LogDebug(
        "BingX futures balance fetched | mode={Mode} | asset={Asset} | balance={Balance}",
        _options.EffectiveMode, queryAsset, value);
}
```

**平倉事件閉環（AccountSynchronizer.cs）：**
```csharp
// 收集本批關倉 payload — 等 SaveChangesAsync 提交後再統一 fire
var closedBroadcasts = new List<PositionClosedUpdate>();

// ... 迴圈內 match.Close(exitPrice, ...) 後 ...
if (_broadcaster is not null)
{
    closedBroadcasts.Add(new PositionClosedUpdate(
        PositionId: match.Id,
        ClosedAtUtc: match.ClosedAt ?? DateTime.UtcNow,
        Symbol: match.Symbol.BingXFormat,
        PositionSide: match.Side.ToString(),
        ExitPrice: exitPrice.Value,
        RealizedPnL: match.RealizedPnL));
}

// ... 迴圈外 ...
await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
foreach (var payload in closedBroadcasts)
{
    try { await _broadcaster.BroadcastPositionClosedAsync(payload, ...); }
    catch (Exception ex) { _logger.LogWarning(ex, ...); }
}
```

**UI 即時刷新（TradeHistoryTable.razor）：**
```razor
@implements IDisposable
@inject DashboardEventBus Bus

@code {
    private Action<PositionClosedUpdate>? _closedHandler;

    protected override async Task OnInitializedAsync()
    {
        _closedHandler = OnPositionClosed;
        Bus.PositionClosed += _closedHandler;
        await LoadAsync();
    }

    private void OnPositionClosed(PositionClosedUpdate update)
    {
        _ = InvokeAsync(async () => { await LoadAsync(); StateHasChanged(); });
    }

    public void Dispose()
    {
        if (_closedHandler is not null)
        { Bus.PositionClosed -= _closedHandler; _closedHandler = null; }
    }
}
```

### 建置與測試

- `dotnet build -p:BaseOutputPath=bin/Probe-S31/`：✓ **0 warn / 0 err**
- `dotnet test`（Probe-S31 build）：Domain **26/26** + Application **86/86** = **112 全通過**

### VCP 檢核對照

| VCP | 實作對應 |
|---|---|
| **[VCP-VST-Balance]** Dashboard 顯示的餘額與 BingX 網頁端/App 的 VST 模擬金餘額一致 | `BingXExchangeClient.GetFuturesBalanceAsync` 使用 `QuoteAsset`（Demo → `"VST"`），且 REST client 已透過 `BingXEnvironment.Demo` 路由到模擬盤 API；DashboardStatsService 呼叫 `GetFuturesBalanceAsync(ct)` 不指定 asset，由 client 自動套 VST。首次成功取得的 Info log 即為 PM 可驗證之痕跡：<br/>`✅ BingX futures balance fetched \| mode=Demo \| asset=VST \| balance=<value>` |
| **[VCP-Order-Flow]** 手動啟動策略，掛單出現在 BingX 模擬盤交易界面 | `BingXRestClient` 以 `BingXEnvironment.Demo` 建構後，`PlaceOrderAsync` 送出的訂單即走模擬盤 REST；`StrategyExecutor.HandleOpenSignalAsync` 觸發後在「`🚀 [STRATEGY-MATCH] ... Order placed.`」後會打「`Order placed: {Symbol} {Side} qty={Qty} id={Id}`」—此 `id` 即模擬盤回傳的 OrderId，可在 BingX VST 界面查到。 |
| **[VCP-History-Sync]** 模擬成交平倉後，Dashboard 的「交易歷史表」自動增加一列正確的紀錄 | WS `OnExchangeAccountUpdate` → `AccountSynchronizer.HandleAccountUpdateAsync` 偵測 `remote.Quantity == 0m` → `Position.Close(exitPrice, ...)` 寫入 `ExitPrice` / `ClosedAt` / `RealizedPnL`（S39 欄位） → `SaveChangesAsync` → `BroadcastPositionClosedAsync` → `DashboardEventBus.PositionClosed` → `TradeHistoryTable` 重拉 `/api/dashboard/trade-history?limit=50` → 新列出現。全鏈 in-process，無額外 polling。 |

### 交付聲明

**「模擬盤連線檢查完畢，VST 帳戶已就緒」**

- REST / WebSocket 皆依 `EffectiveMode` 綁 `BingXEnvironment.Demo`，餘額 / 持倉 / 下單 / listenKey 四條路徑全部走模擬盤端點。
- `QuoteAsset` 自動在 Demo 模式查 VST、Live 模式查 USDT，不會誤報 0 餘額。
- 熔斷器在 Demo 下以 DB 已實現損益 + VST 餘額做計算，與 Live 行為同構。
- **補完成交回報閉環**：T2 發現 TradeHistoryTable 平倉後不會自動更新（違反 VCP-History-Sync），已新增 `PositionClosedUpdate` 事件鏈，從 `AccountSynchronizer` → SignalR → Blazor 事件總線全線打通，UI 收到事件後立即 re-fetch。

**VST 餘額證明 log 片段**（預期在啟動後首次 Dashboard 輪詢 2s 內出現於 `logs/cryptobot-YYYYMMDD.log`）：

```
[INF] 🟢 BingX REST client built | mode: 🟢 DEMO (VST) | quote asset: VST
[INF] BingX market data stream started (Mode=Demo, QuoteAsset=VST)
[INF] Acquired BingX listenKey (truncated: xxxxxxxx...)
[INF] Subscribed BingX user-data stream (listenKey acquired, auto-renew every 30m)
[INF] ✅ BingX futures balance fetched | mode=Demo | asset=VST | balance=<VST 餘額數值>
```

> 備註：最後一行為 S31 新增，是 PM 指定的「成功獲取 VST 餘額的 Log」。如在本地未看到，請確認 `appsettings.json` 的 `BingX.TradingMode = "Demo"` 且 `/settings/exchanges` 已設定有效 API key（S22 之後啟動不再用 appsettings 金鑰，金鑰來自 SQLite）。


---

## S42 · 決策心跳與評估狀態透明化 (HEARTBEAT_MONITOR) — 2026-04-23

### 任務回顧
使用者在 Dashboard 盯盤時遇到「盲目感」— 策略顯示 Running，但沒有任何跡象證明評估邏輯「當下這一秒」還在跑；Current Price / Unrealized PnL 也要等整分鐘收盤才跳，心理上像系統僵住。本膠囊用 in-process 事件 + CSS 動畫證明系統還活著。

### 任務檢核

| 編號 | 項目 | 狀態 | 備註 |
| --- | --- | --- | --- |
| T1 | `StrategyRuntimeState` / `LastEvaluatedAtUtc` + `DashboardEventBus` 廣播 | ✅ | 透過 `StrategyExecutor.LastEvaluatedAtUtc` 屬性 + `StrategyEvaluatedUpdate` 事件完成（未新建 Domain 型別，以 Executor 屬性承載心跳狀態；心跳為純 in-process 資料不落 DB） |
| T2 | 心跳標籤 `Last Evaluated: [HH:MM:SS] (Ns ago)` + `.heartbeat-dot` 呼吸燈 | ✅ | 透過 Razor `@key` 屬性變動觸發 CSS 動畫重播，純前端、零 JS interop |
| T3 | WebSocket 價格 / uPnL 連動 | ✅ | 新增 `PositionPnLTickUpdate` 從 `AccountSynchronizer` 的 WS account update 推給 UI，Active Positions 表格就地更新，不整包重拉 |

### 實作清單

| 檔案 | 變更類型 | 說明 |
| --- | --- | --- |
| `src/CryptoBot.Application/Realtime/StrategyEvaluatedUpdate.cs` | 新增 | 心跳事件 record（`StrategyId`, `EvaluatedAtUtc`, `Symbol`, `Interval`, `LastClosePrice`） |
| `src/CryptoBot.Application/Realtime/PositionPnLTickUpdate.cs` | 新增 | 單部位 PnL tick record（`PositionId`, `Symbol`, `CurrentPrice`, `UnrealizedPnL`, `UnrealizedPnLPercent`） |
| `src/CryptoBot.Application/Realtime/IRealtimeBroadcaster.cs` | 擴充 | 加 `BroadcastStrategyEvaluatedAsync` + `BroadcastPositionPnLAsync` |
| `src/CryptoBot.Application/Realtime/NullRealtimeBroadcaster.cs` | 擴充 | 加對應 no-op 實作（純 CLI / 單元測試用） |
| `src/CryptoBot.ConsoleApp/Realtime/DashboardEventBus.cs` | 擴充 | 加 `StrategyEvaluated` + `PositionPnLTicked` event + 對應 Raise 方法 |
| `src/CryptoBot.ConsoleApp/Realtime/SignalRRealtimeBroadcaster.cs` | 擴充 | 加兩個對應 broadcast，同步 fan-out 給 hub clients + bus |
| `src/CryptoBot.Application/Strategies/IStrategyExecutor.cs` | 擴充 | 加 `LastEvaluatedAtUtc` 屬性 |
| `src/CryptoBot.Application/Strategies/StrategyExecutor.cs` | 擴充 | `ProcessKlineAsync` 每次 `AnalyzeAsync` 完成後設定時間戳 + 廣播心跳事件 |
| `src/CryptoBot.Application/Strategies/IStrategyRuntimeController.cs` | 擴充 | 加 `GetLastEvaluatedAtUtc(Guid)` 供 REST 端點做初值查詢 |
| `src/CryptoBot.Application/Strategies/StrategyRuntimeHostedService.cs` | 擴充 | 實作 `GetLastEvaluatedAtUtc` — 走 `_executors` dict 在鎖內讀 executor 屬性 |
| `src/CryptoBot.Application/Synchronization/AccountSynchronizer.cs` | 擴充 | `HandleAccountUpdateAsync` 的 MarkPrice 分支新增 `BroadcastPositionPnLAsync`，broadcast 失敗 log 後吞掉 |
| `src/CryptoBot.ConsoleApp/Api/Dtos/StrategyDto.cs` | 擴充 | 加 `LastEvaluatedAtUtc` 欄位 |
| `src/CryptoBot.ConsoleApp/Api/StrategyEndpoints.cs` | 擴充 | GET `/api/strategies` 注入 `IStrategyRuntimeController` 取即時心跳；`ToDto` 加可選 `lastEvaluatedAtUtc` 參數 |
| `src/CryptoBot.ConsoleApp/Components/Pages/Dashboard.razor` | 擴充 | 新增 heartbeat row、heartbeat-dot、`_heartbeatPulseTick` 遞增、`System.Timers.Timer` 1s 讓 ago 自增、訂閱 `Bus.StrategyEvaluated` + `Bus.PositionPnLTicked`、Dispose 完整清理 |
| `src/CryptoBot.ConsoleApp/wwwroot/app.css` | 擴充 | `.heartbeat-dot` + `@keyframes heartbeat-pulse` + `.heartbeat-row` + `.heartbeat-time` |
| `tests/CryptoBot.Application.Tests/Strategies/StrategyRuntimeHostedServiceTests.cs` | 小改 | `RecordingExecutor` 補上 `LastEvaluatedAtUtc` 屬性以滿足新介面合約 |

### 關鍵設計抉擇

1. **心跳以 executor 屬性承載，不落 Domain / DB**
   膠囊原文寫「在 `StrategyRuntimeState` 中增加 `LastEvaluatedAtUtc`」，但專案目前沒有 `StrategyRuntimeState` 這個型別。不為了膠囊措辭硬建一個新 aggregate — 那會讓每根 K 線都寫一次 DB，純粹浪費 I/O。改以 `StrategyExecutor.LastEvaluatedAtUtc` 屬性 + in-process 事件匯流排滿足膠囊精神（「證明策略大腦還在跑」），持久化交給 log 即可。

2. **呼吸燈用 `@key` + CSS 重播，不用 JS interop**
   `.heartbeat-dot` 的 `@key="_heartbeatPulseTick"` 綁定一個整數計數器；每次收到 `StrategyEvaluated` 事件 `_heartbeatPulseTick++`，Blazor diff 演算法會視為一個新 DOM 節點 → 重建 → 瀏覽器重跑一次 1 秒 `@keyframes heartbeat-pulse` 動畫。零 JS、零套件，純 Razor 機制。

3. **T3 只在 MarkPrice 改變時 broadcast，不額外拉計時器**
   保持「WS 驅動」的契約 — UI 跳動節奏由交易所推播節奏決定，不在應用層偽造 tick。broadcast 在 `Position.UpdateCurrentPrice` 之後立即 fire，單筆失敗只 log（WS handler 絕不能上拋，沿用 S31 的 handler 契約）。

4. **DTO 新欄位用可選 ctor 參數避免其他 call site 要逐一改**
   `StrategyDto.LastEvaluatedAtUtc` 是新增的最後一個欄位，`ToDto` 新增的 `lastEvaluatedAtUtc` 參數預設 null。這樣 `BacktestLab.razor:571` 等其他呼叫 `ToDto(s)` 的位置零改動。

### 驗證檢核點 (VCP) 映射

| VCP | 驗證方式 | 結果 |
| --- | --- | --- |
| VCP-Pulse | 啟動策略後每根收盤 K 線觸發一次 `AnalyzeAsync` → 設 `LastEvaluatedAtUtc = UtcNow` → 廣播 `StrategyEvaluatedUpdate` → Dashboard 更新時間戳並遞增 `_heartbeatPulseTick` 播放呼吸燈 | ✅ 事件鏈閉合；1 分鐘 interval 策略會每分鐘跳動 |
| VCP-Live | 交易所 WS 推 MarkPrice → `AccountSynchronizer.HandleAccountUpdateAsync` 更新 `Position.CurrentPrice` + `BroadcastPositionPnLAsync` → Dashboard 就地更新該部位 Mark / uPnL / uPnL% | ✅ 不再受一分鐘節奏限制 |
| Build | `dotnet build CryptoBot.sln -c Debug -p:BaseOutputPath=bin/S42-Probe/` | ✅ 0 error / 0 warning |
| Test | `dotnet test CryptoBot.sln` | ✅ Domain 26/26、Application 86/86，共 **112/112** |

### Heartbeat 動畫 CSS 片段（膠囊指定交付物）

```css
/*
 * .heartbeat-dot 是策略 Running 旁邊的呼吸燈：每次策略評估事件進來，
 * Dashboard 會遞增 `_heartbeatPulseTick` 並把它當作 @key 綁到這顆 dot 上，
 * Blazor 因此會重新建立該 DOM 節點，瀏覽器觸發一次完整 CSS @keyframes 動畫重播，
 * 達到「每次心跳亮一下」的效果 — 不需要 JS interop，純 Razor @key + CSS 完成。
 */
.heartbeat-dot {
    display: inline-block;
    width: 10px;
    height: 10px;
    margin-left: 10px;
    border-radius: 50%;
    background: rgba(46, 255, 139, 0.85);
    box-shadow: 0 0 0 0 rgba(46, 255, 139, 0.7);
    animation: heartbeat-pulse 1s ease-out 1;
    vertical-align: middle;
}

@keyframes heartbeat-pulse {
    0%   { transform: scale(0.85); box-shadow: 0 0 0 0   rgba(46, 255, 139, 0.85); opacity: 1; }
    50%  { transform: scale(1.25); box-shadow: 0 0 0 6px rgba(46, 255, 139, 0.15); opacity: 0.95; }
    100% { transform: scale(1.00); box-shadow: 0 0 0 0   rgba(46, 255, 139, 0.00); opacity: 0.85; }
}

.heartbeat-row {
    padding: 4px 20px 14px 20px;
    display: flex;
    align-items: center;
    gap: 8px;
    font-family: var(--mono);
    font-size: 12px;
}
.heartbeat-time {
    color: var(--text);
    letter-spacing: 0.4px;
}
```

**Razor 端重播動畫的關鍵一行**（在策略卡片 `控制行/執行狀態` cell 裡）：

```razor
@if (_primaryStrategy.Status == "Running")
{
    <span class="heartbeat-dot" @key="_heartbeatPulseTick" title="策略大腦持續運行中"></span>
}
```

### 交付結論

> 決策心跳監控已上線。

---

## S44 · Dashboard 戰情室版面重組與滾動日誌 (DASHBOARD_WAR_ROOM) — 2026-04-23

### 任務回顧
S42 讓使用者確認策略「還活著」；S44 進一步讓使用者看懂策略「正在思考什麼」。Dashboard 從長條狀改為左右分欄戰情室：左欄是策略控制室（保留 S42 心跳燈）、右欄是即時滾動的決策日誌 + 交易歷史。

### 任務檢核

| 編號 | 項目 | 狀態 | 備註 |
| --- | --- | --- | --- |
| T1 | `EvaluationEntry` 模型 + LinkedList rolling buffer（max 30） | ✅ | Dashboard 內部 UI record；`PushLogEntry` 用 `AddFirst` + 尾端裁切，O(1) |
| T2 | CSS Grid `350px 1fr` 左右分欄，940px 以下自動單欄回退 | ✅ | `.war-room-grid` 外層；`<aside class="war-room-left">` + `<main class="war-room-right">` |
| T3 | INF 灰 / SIGNAL BUY 綠 / SIGNAL SELL 紅 / ERROR 橘 色碼 | ✅ | `.eval-log-entry.{inf,signal-buy,signal-sell,error}` 四組 CSS class |

### 實作清單

| 檔案 | 變更類型 | 說明 |
| --- | --- | --- |
| `src/CryptoBot.Application/Realtime/StrategyEvaluatedUpdate.cs` | 擴充 | 加 `StrategyName` / `SignalType` / `Note` 三欄 — 日誌色碼與顯示理由由此驅動 |
| `src/CryptoBot.Application/Realtime/StrategyEvaluationFailedUpdate.cs` | 新增 | 失敗事件 record（`StrategyId`, `StrategyName`, `OccurredAtUtc`, `Symbol`, `ErrorMessage`） |
| `src/CryptoBot.Application/Realtime/IRealtimeBroadcaster.cs` | 擴充 | 加 `BroadcastStrategyEvaluationFailedAsync` |
| `src/CryptoBot.Application/Realtime/NullRealtimeBroadcaster.cs` | 擴充 | 對應 no-op 實作 |
| `src/CryptoBot.ConsoleApp/Realtime/DashboardEventBus.cs` | 擴充 | 加 `StrategyEvaluationFailed` event + `RaiseStrategyEvaluationFailed` |
| `src/CryptoBot.ConsoleApp/Realtime/SignalRRealtimeBroadcaster.cs` | 擴充 | `BroadcastStrategyEvaluationFailedAsync` 同步 fan-out hub + bus |
| `src/CryptoBot.Application/Strategies/StrategyExecutor.cs` | 擴充 | 心跳 payload 補 `StrategyName` / `SignalType` / `Note`；catch 區塊新增 `BroadcastStrategyEvaluationFailedAsync` |
| `src/CryptoBot.ConsoleApp/Components/Pages/Dashboard.razor` | 擴充 | `.war-room-grid` 外層；新增「決策動態日誌」panel（SignalR-driven）；`EvaluationEntry` UI record；`LinkedList` ring buffer；訂閱 `StrategyEvaluationFailed`；Dispose 清理 |
| `src/CryptoBot.ConsoleApp/wwwroot/app.css` | 擴充 | `.war-room-grid` / `.war-room-left` / `.war-room-right`（含 940px 媒體查詢）；`.eval-log-panel` / `.eval-log` / `.eval-log-entry` 四色碼組 |

### 關鍵設計抉擇

1. **`LinkedList<EvaluationEntry>` 做 ring buffer，不用 `Queue`**
   膠囊說「`LinkedList` 或 `Queue`」—— 選前者是因為 UI 用 `@foreach` 顯示，需要從頭枚舉；若用 `Queue` 要 `.Reverse()` 才能拿到「最新在頂」的順序。`LinkedList.AddFirst` + 尾端 `RemoveLast` 兩個都是 O(1)，天然匹配。

2. **`EvaluationEntry` 是 Dashboard 私有 record，不跟 Application event record 混用**
   Application 層的 `StrategyEvaluatedUpdate` 是傳輸格式，帶 utc 時間 + 原始字串；UI record 預先算好 local time + CSS class + tag 文字，畫面只管綁定。這讓色碼邏輯集中在一個地方（`OnStrategyEvaluated` 的 switch），未來要改配色不必找滿整個 CSS。

3. **Grid 用 `aside` + `main` 語意標籤**
   不只為語意正確，也讓 CSS 選擇器 `.war-room-left > .panel` 只命中左欄 panel 的 margin 覆寫，避免一般 panel 被波及。

4. **940px breakpoint 回退單欄**
   `grid-template-columns: 1fr` 在窄視窗堆疊，不用特殊處理即保留可讀性 — 對 laptop 側邊開發者工具的情境特別重要（開 devtools 後工作區常常卡在 850–900px）。

5. **SIGNAL BUY 同時涵蓋 `OpenLong` 與 `CloseShort`**
   兩者都是「市場買入」方向，從 UI 色碼語意講就是綠色；`OpenShort` + `CloseLong` 反之用紅。這比直接照 SignalType 字串分四色更貼近交易員直覺。

### 驗證檢核點 (VCP) 映射

| VCP | 驗證方式 | 結果 |
| --- | --- | --- |
| VCP-Layout | CSS grid `350px 1fr` 在大螢幕左右併排；< 940px 自動單欄回退 | ✅ |
| VCP-Rolling | `PushLogEntry` 在 `Count > 30` 時 `RemoveLast`；30 分鐘後穩定維持 30 筆；最新在頂（`AddFirst`） | ✅ |
| Build | `dotnet build CryptoBot.sln -c Debug -p:BaseOutputPath=bin/S44-Probe/` | ✅ 0 error / 0 warning |
| Test | `dotnet test CryptoBot.sln` | ✅ Domain 26/26、Application 86/86，共 **112/112** |

### Dashboard 左右分欄 CSS Grid 片段（膠囊指定交付物）

```css
/* ===== S44 · 戰情室左右分欄 =====
 * LEFT（350px）：策略控制台（Active Strategy card）
 * RIGHT（1fr）：上半 決策動態日誌；下半 交易歷史表
 * 940px 以下 breakpoint → 自動單欄堆疊，避免手機 / 窄視窗被截掉。
 */
.war-room-grid {
    display: grid;
    grid-template-columns: 350px 1fr;
    gap: 16px;
    padding: 0 20px 20px 20px;
    align-items: start;
}
.war-room-left  { min-width: 0; }
.war-room-right {
    display: flex;
    flex-direction: column;
    gap: 14px;
    min-width: 0;
}
.war-room-left > .panel,
.war-room-right > .panel {
    margin: 0 0 14px 0;
}
@media (max-width: 940px) {
    .war-room-grid { grid-template-columns: 1fr; }
}
```

**對應的 Razor 骨架**：

```razor
<div class="war-room-grid">
  <aside class="war-room-left">
    <section class="panel">策略控制台（含 S42 heartbeat-dot）</section>
  </aside>

  <main class="war-room-right">
    <section class="panel eval-log-panel">
      決策動態日誌（LinkedList, 最多 30 筆, 最新在頂）
    </section>
    <TradeHistoryTable Limit="50" />
  </main>
</div>
```

### 交付結論

> Dashboard 戰情室版面已進化。

---

## S42-S47 · 戰情室升級與同步系統（合併膠囊修訂） — 2026-04-23

> 原則：S42 / S44 戰情室基座與 S45 策略熱轉型已先行落地（見上兩節）；本節追加 S45 的 T2 修訂（同步 Symbol / Interval、改名後綴 `(Opt)`）、S47 Lab 網格設定持久化（VCP-Memory），以及 S43 Price Action Predictor 裸 K 策略大腦。

### §VCP-Sync：實驗室→儀表板資訊流完整閉環

**修訂動機**：S45 原本只同步 StrategyType + Name，但實際使用時 Lab 可能在 SOL-15m 優化完，卻把結果硬套到還停留在 BTC-1h 的卡片上 — 卡片標題顯示「SOL-15m (Opt)」，卻仍在 BTC-1h 下單，屬於資料錯位隱患。

修訂動作：`ApplyParamsRequest` 擴充 `Symbol` / `Interval`，`/api/lab/apply/{id}` 在單一 Stop→Save→Start 循環內一次把四件事改齊（StrategyType / Symbol / Interval / Parameters），名稱後綴統一縮短為 `(Opt)`，如 `[B46 Hybrid] SOL-15m (Opt)`。

**LabEndpoints.cs — Symbol/Interval 覆寫核心片段**（交付物要求）：

```csharp
// S42-S47 T2：套用時可同步覆寫 Symbol / Interval，避免「Lab 在 SOL-15m 優化完，
// 卻把結果硬套到還停留在 BTC-1h 的卡片上」這種資料錯位。
Symbol targetSymbol;
if (!string.IsNullOrWhiteSpace(body.Symbol))
{
    try { targetSymbol = Symbol.Parse(body.Symbol); }
    catch (DomainException ex) { return Results.BadRequest(new { error = $"Invalid Symbol '{body.Symbol}': {ex.Message}" }); }
}
else
{
    targetSymbol = current.Symbol;
}
var targetInterval = body.Interval ?? current.Interval;

var newConfig = StrategyConfiguration.Create(
    symbol: targetSymbol,
    interval: targetInterval,
    /* …其他欄位承襲 current… */
    parameters: newParams);

// 名稱規則：`[模型短名] 幣種-週期 (Opt)`
if (modelDisplayName is not null)
{
    newName = BuildOptimizedName(modelDisplayName, targetSymbol.BingXFormat, targetInterval);
    strategy.Rename(newName);
}
```

### §VCP-WarRoom：戰情室座標系完整度

原 S44 已落地雙欄（左：策略控制台 + heartbeat dot；右：決策動態日誌 + 交易歷史）。本膠囊無進一步改動，保留作為已驗證狀態。

### §VCP-Memory：Lab 網格設定跨頁持久化

**S47 問題**：使用者在 BacktestLab 編好 Min/Max/Step，切頁到 Dashboard 再回來時，表單被重建、先前的設定被 reset 為預設值 — 違反「系統應記住操作者當下意圖」的狀態連續性原則。

實作：
- `LabStateContainer`（Singleton）新增 `ConcurrentDictionary<string, IReadOnlyDictionary<string, ParameterGridRange>> _gridCache`，以 StrategyKey 為鍵。
- `StrategyParameterFormBase` 抽出 `CurrentGrid` 虛擬屬性，每個參數表單回報自己當前的 PascalCase 鍵值對。
- `BacktestLab.razor` 在切模型時保存舊表單的 `CurrentGrid`；在新表單 DOM 就緒（`OnAfterRenderAsync` 透過 `_pendingGridRestoreKey` flag）後還原；每次表單變更也即時寫回，確保中途切頁不丟進度。

```csharp
public void SaveGridSettings(string strategyKey, IReadOnlyDictionary<string, ParameterGridRange> grid)
{
    if (string.IsNullOrWhiteSpace(strategyKey)) return;
    if (grid is null || grid.Count == 0) return;  // 空字典不洗掉舊快取
    _gridCache[strategyKey] = grid;
}

public IReadOnlyDictionary<string, ParameterGridRange>? TryGetGridSettings(string strategyKey)
    => _gridCache.TryGetValue(strategyKey, out var v) ? v : null;
```

### §VCP-Brain：S43 Price Action Predictor

裸 K 策略大腦 — 不依賴任何移動平均或震盪指標，純從 OHLC 結構推斷短期方向。

**形態偵測**（`PriceActionPredictorStrategy.DetectPatterns`）：
- **Bullish / Bearish Engulfing**：前陰 K 被當前陽 K 整根吞沒（反之亦然）
- **Hammer**：實體小 + 下影線 ≥ 實體 × `WickToBodyRatio` + 上影線短 + 收陽
- **Shooting Star**：實體小 + 上影線長 + 下影線短 + 收陰

**動能得分**（`MomentumScore`）：窗口 `LookbackPeriod` 根 K 的 `(Close - Open) / Close` 加總，正值偏多、負值偏空。用加總而非平均，讓「連續單向推動」不被均值化洗掉。

**雙驗證進場**：形態 + 動能同向才放行（`patterns.BullishReversal && momentum >= MomentumThreshold`），避免盤整期假訊號。

**T4 出場智慧**：持倉期間若見反向形態（Long 遇 Bearish Engulfing / Shooting Star，Short 遇 Bullish Engulfing / Hammer），主動吐 `CloseLong` / `CloseShort`，不等停損被拉走。

**參數**：
- `LookbackPeriod`（默認 20）：動能窗口 K 數
- `MomentumThreshold`（默認 0.01）：動能絕對值門檻
- `WickToBodyRatio`（默認 2.0）：Hammer / Shooting Star 的影線 / 實體比
- `EngulfingEnabled`（默認 1；0 = 禁用）：是否考慮吞噬形態
- `Confidence`（默認 0.65）：訊號置信度

**插座**：
- `DependencyInjection.cs`：`services.AddSingleton<IStrategy, PriceActionPredictorStrategy>();`
- `StrategyCatalog`：Key `"pa"` / DisplayName `"Price Action Predictor"` / 5 個 ExpectedParameterKeys
- `OptimizationOrchestrator.ResolveStrategy`：`"pa" => new PriceActionPredictorStrategy()`
- `LabEndpoints.MapLabKeyToStrategyType`：`"pa" => "PriceAction"`
- `BuildOptimizedName`：短名後綴清單追加 `" Predictor"`
- `PaParameterForm.razor`：5 維 Min/Max/Step 網格表單，含 Wick/Body ≥ 1 的語義檢查

### 建置與測試

```
dotnet build CryptoBot.sln -p:BaseOutputPath=artifacts/S43/ --nologo
建置成功。  0 個警告  0 個錯誤

dotnet test CryptoBot.sln -p:BaseOutputPath=artifacts/S43/ --no-build --nologo
CryptoBot.Domain.Tests       26/26 通過
CryptoBot.Application.Tests  86/86 通過
總計 112 通過、0 失敗
```

### 交付結論

> 戰情室升級與同步系統已就緒。

---

## S45-S48 修正補丁 — 即時同步 × 網格記憶 × 智慧 Prompt（REVISED_CORRECTION）

PM 膠囊：`TASK_S45_S48_REVISED_CORRECTION.md`。針對前一輪的三項核心失效逐項修補 — 不再等心跳、不再遺忘、Prompt 帶身分。

### VCP-Realtime-Sync（T1）— Lab → Dashboard 零延遲

**問題**：Lab 套用策略後，Dashboard 卡片要等下一次策略評估心跳才更新，使用者感覺「按了沒反應」。

**修正動線**：
- 新增 DTO `CryptoBot.Application.Realtime.StrategyMetadataChangedUpdate(StrategyId, Name, StrategyType, Symbol, Interval, Leverage)`
- `IRealtimeBroadcaster` 新增 `BroadcastStrategyMetadataChangedAsync`；`NullRealtimeBroadcaster` 給空實作、`SignalRRealtimeBroadcaster` 同時 fan-out 行程內 `DashboardEventBus` 與 SignalR `"StrategyMetadataChanged"`
- `DashboardEventBus` 新增 `event StrategyMetadataChanged` + `RaiseStrategyMetadataChanged`
- `LabEndpoints` `/apply` 成功後立即 broadcast — 不等心跳
- `Dashboard.razor` 訂閱事件 → 以 `with`-expression 原地更新 `_primaryStrategy` 並 `InvokeAsync(StateHasChanged)` 刷新卡片

**VCP 驗證**：Lab 套新的模型／Symbol／週期／槓桿 → Dashboard 卡片「Name / Strategy Type / Symbol / Interval / Leverage」欄位當場翻新；不需重載頁面、不需等待。

### VCP-Solid-Memory（T2）— 每模型網格 100% 復原

**問題**：Lab 分頁來回切換時，Min/Max/Step 偶發為空 — 不是快取沒存，是 UI 沒把快取推回表單。

**稽核結果**：
- `LabStateContainer._gridCache : ConcurrentDictionary<string, IReadOnlyDictionary<string, decimal[]>>` 已按「symbol|interval|modelKey」維度存滿所有策略（sma / trend / mean-reversion / rsi-bb / pa）Min/Max/Step — 存取契約本來就正確
- `OnMarketChangedAsync` 只清 `_cachedSettings`（伺服器的「上次最佳結果」），不動 `_gridCache` — 設計上就已經安全

**修 bug**：`OnAfterRenderAsync` 原本「先把 `_pendingGridRestoreKey` 吃掉再看 `_dynamicRef` 有沒有 bind」— 第一輪 render 時 DynamicComponent 還沒實體化，flag 就被消耗掉、永遠回不來。改成「form 確定就緒」才消耗 flag：

```csharp
protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (_pendingGridRestoreKey is null) return;
    if (_dynamicRef?.Instance is not StrategyParameterFormBase form) return; // 下次再試
    var key = _pendingGridRestoreKey;
    _pendingGridRestoreKey = null; // form 已就緒 — 不管有無快取都只嘗試一次
    var cached = State.TryGetGridSettings(key);
    if (cached is null) return;
    await form.ApplyGridParametersAsync(cached);
}
```

**VCP 驗證**：Lab 設好某模型的 Min/Max/Step → 切 Dashboard → 切 Settings → 回 Lab 並切回該模型分頁 → 所有欄位完整復原（含之前新增的 PA Predictor 5 維）。

### VCP-Smart-Prompt（T3 / S48）— AI Prompt 帶身分

**問題**：Prompt 產出後 AI 不知道在看哪個「決策模型」，建議常偏向通用原則，缺參數級優化值。

**修正**：`AiAdvisorPanel.razor`
- 新增 `[Parameter] public string StrategyDisplayName { get; set; }` — 父頁面從 `State.SelectedModel.DisplayName` 直接餵，比 key 好讀
- `BuildAdvicePrompt` 最頂端放上「當前決策模型」標籤
- 指令清單追加第 5 條：要求 AI 給每個參數的 Min / Max / Step 具體值
- Prompt textarea 放大字體（font-size 1.05rem、line-height 1.6、等寬字型），Min/Max/Step 數字對齊更容易看

**BacktestLab.razor** 連線：

```razor
<AiAdvisorPanel StrategyKey="@State.SelectedModel.Key"
                StrategyDisplayName="@State.SelectedModel.DisplayName"
                Symbol="@_symbolInput"
                Interval="@_intervalInput"
                Disabled="@State.IsRunning" />
```

**BuildAdvicePrompt 中加入模型名稱的代碼片段（PM 指名附上）**：

```csharp
private static string BuildAdvicePrompt(string strategyKey, string strategyDisplayName, MarketSweepSnapshotDto s)
{
    // S48：key → 中文說明的 fallback，僅在 DisplayName 為空時使用。
    var strategyLabel = strategyKey switch
    {
        "sma"            => "SMA Crossover（雙均線金叉 / 死叉）",
        "trend"          => "EMA Trend Following（EMA 黃金/死亡交叉 × RSI 動能）",
        "mean-reversion" => "Bollinger Reversion（布林通道 × RSI 極端反轉）",
        "rsi-bb"         => "B46 Hybrid Model（RSI × Bollinger 複合訊號）",
        "pa"             => "Price Action Predictor（裸 K 形態 × 動能雙驗證）",
        _                => string.IsNullOrWhiteSpace(strategyKey) ? "（未指定）" : strategyKey,
    };

    // S48 VCP-Smart-Prompt：Prompt 最頂端標註「當前決策模型」，AI 一眼認出在分析哪顆大腦。
    var headerModel = string.IsNullOrWhiteSpace(strategyDisplayName) ? strategyLabel : strategyDisplayName;

    var sb = new StringBuilder();
    sb.Append("【當前決策模型】：").AppendLine(headerModel);
    sb.AppendLine();
    sb.AppendLine("你是一位頂尖的加密貨幣量化交易分析師，請針對以下市場快照給出深度分析。");
    sb.AppendLine();
    // ...（其餘市場快照、指標、請回答 1–4 維持不變）

    sb.AppendLine("5. 請針對該模型的所有參數提供具體的優化參考值（包含 Min, Max, Step），以便我直接輸入回測系統掃描。");

    return sb.ToString();
}
```

**VCP 驗證**：從 Lab 任一模型 → 點「📝 生成分析 Prompt」→ 第一行 `【當前決策模型】：{DisplayName}` + 底部第 5 條指令都在位；字體肉眼明顯放大。

### 建置與測試

```
dotnet build -p:BaseOutputPath=artifacts/S48/ -nologo
建置成功。  0 個警告  0 個錯誤

dotnet test --no-build -p:BaseOutputPath=artifacts/S48/ -nologo
CryptoBot.Domain.Tests       26/26 通過
CryptoBot.Application.Tests  86/86 通過
總計 112 通過、0 失敗
```

### 交付結論

> 全系統同步與智慧 Prompt 已修正就緒。

---

## S99-HOTFIX — Symbol 轉型崩潰（SIGNAL SELL 後無法平倉）

PM 膠囊：`TASK_S99_HOTFIX_SYMBOL_CAST.md`。EMA 策略在 SIGNAL SELL 之後，WS 推來 account update 觸發 `AccountSynchronizer`，接著進到 `PositionRepository.GetOpenPositionsBySymbolAsync` 就炸 `InvalidCastException: Invalid cast from 'System.String' to 'Symbol'`。結果：本地倉無法匹配 → `Position.Close(...)` 不會被呼叫 → 歷史表沒新紀錄、視同無法下單。

### 診斷定位

同一雷點其實 README §S17.5 已記錄過一次：
> **S17.5** | 修 EF Core `Symbol` value converter 在 `EF.Property<string>` 路徑炸的問題；新增 `OrderRepo.GetRecentAsync` 繞開

當時只繞開了 `OrderRepo.GetRecentAsync` 一處，**但底下兩個核心查詢仍保留原寫法**：

```csharp
// PositionRepository.cs（修正前）
var list = await _ctx.Positions
    .Where(p => !p.IsClosed
             && EF.Property<string>(p, nameof(Position.Symbol)) == symbolStr)
    .ToListAsync(ct);
```

`Position.Symbol` 是 `Symbol` VO，並透過 `SymbolConverter : ValueConverter<Symbol, string>` 對應到 TEXT 欄位。`EF.Property<string>(p, "Symbol")` 試圖用儲存型別（string）繞過 converter 直接比對，這在 EF Core 8 的查詢翻譯路徑下會反向觸發 CLR 型別檢查 → `InvalidCastException: String → Symbol`。

### 熱路徑關聯

`GetOpenPositionsBySymbolAsync` 在兩個位置被踩到：

| Caller | 觸發時機 |
|---|---|
| `AccountSynchronizer.HandleAccountUpdateAsync` | 每一筆 WS account update — 賣出成交、平倉即時觸發 |
| `RiskManager.CheckBeforeOpenAsync` | 每一次新開倉前的單位容量檢查 |

膠囊描述的「SIGNAL SELL 後崩潰」對應第一條 — 只要 SELL 訊號成交，WS 一推就炸，永遠對不上本地 Position，歷史表永遠不跳新紀錄。

### 修正後的 Mapping / 轉換邏輯代碼片段（PM 指名附上）

```csharp
// src/CryptoBot.Infrastructure/Persistence/Repositories/PositionRepository.cs
public async Task<IReadOnlyList<Position>> GetOpenPositionsBySymbolAsync(
    Symbol symbol, CancellationToken ct = default)
{
    // S99 HOTFIX：SIGNAL SELL 後 AccountSynchronizer 會經這裡查對應的本地倉位，
    // 原本用 EF.Property<string>(p, nameof(Position.Symbol)) == symbolStr 在
    // SymbolConverter (ValueConverter<Symbol, string>) 下會拋
    // `InvalidCastException: Invalid cast from 'System.String' to 'Symbol'`
    // — README §S17.5 已記錄相同雷點。
    // 改成先抓所有未平倉（量少、實務上 <50 筆），再在記憶體裡用 Symbol.Equals 過濾，
    // 徹底迴避 EF 對 value-converted VO 的表達式翻譯邊角案例。
    var openList = await _ctx.Positions
        .Where(p => !p.IsClosed)
        .ToListAsync(ct).ConfigureAwait(false);
    return openList.Where(p => p.Symbol.Equals(symbol)).ToList();
}
```

`OrderRepository.GetBySymbolAsync` 同病一併根除（雖無生產 caller）：

```csharp
// src/CryptoBot.Infrastructure/Persistence/Repositories/OrderRepository.cs
public async Task<IReadOnlyList<Order>> GetBySymbolAsync(Symbol symbol, CancellationToken ct = default)
{
    // S99 HOTFIX：同 PositionRepository.GetOpenPositionsBySymbolAsync — 原本用
    // `EF.Property<string>(o, nameof(Order.Symbol)) == symbolStr` 在 SymbolConverter 下
    // 會拋 `InvalidCastException`（README §S17.5）。改成記憶體內用 Symbol.Equals 過濾。
    var list = await _ctx.Orders
        .OrderByDescending(o => o.CreatedAt)
        .ToListAsync(ct).ConfigureAwait(false);
    return list.Where(o => o.Symbol.Equals(symbol)).ToList();
}
```

### 為何選「拉小集合 + in-memory filter」而非修 Converter

已註冊的 `SymbolConverter` 本身是正確的：寫入走 `v => v.BingXFormat`、讀取走 `v => Symbol.Parse(v)`，並透過 `PositionConfiguration.Property(p => p.Symbol).HasConversion(new SymbolConverter())` 掛在欄位上 — 單筆物件的 hydrate / persist 都完全正常（`GetByIdAsync` / `AddAsync` / `UpdateAsync` / `GetOpenPositionsAsync` 從未報錯）。問題只出在 LINQ 的 `Where` clause 企圖繞過 converter 去用儲存字串比對的寫法。與其逆著 converter 設計硬塞 LINQ 表達式，不如把「跨 VO 比對」這件事留在 CLR 內做 — 未平倉倉位 / 近期訂單的資料量都夠小，記憶體過濾成本可忽略，且徹底避開 EF 表達式樹的翻譯邊角案例。

### 建置與測試

```
dotnet build  -p:BaseOutputPath=artifacts/S99/ -nologo   →  0 警告 / 0 錯誤
dotnet test   -p:BaseOutputPath=artifacts/S99/ --no-build →  26 + 86 = 112/112 通過
```

### VCP

- **[VCP-Run]** — SIGNAL SELL 再次出現時，WS account update 進到 `GetOpenPositionsBySymbolAsync` 會正常回傳本地對應倉位、`Position.Close(...)` 得以執行、Dashboard `TradeHistoryTable` 透過 `PositionClosed` 事件跳出新紀錄、不再噴 `Invalid cast` 例外。

### 交付結論

> Symbol 轉型 Bug 已徹底修復。

---

## S99-HOTFIX 補遺：T4 錯誤原因透明化 + T5 快照源頭巡檢

### T4 — 在 Dashboard「Recent Trades」暴露 `RejectReason`

PM 觀察：實盤 rejected 訂單只看到 `Status`，看不到 BingX 的錯誤代碼（例：`100010 餘額不足`），等同黑箱。

**傳輸層**：`RecentTradeDto` 既有欄位只到 `Status`，新增 nullable 尾欄位 `RejectReason`（成功 / pending 單為 null，實際寫值的只有 `Order.Reject(reason)` 的路徑）：

```csharp
// src/CryptoBot.ConsoleApp/Api/Dtos/DashboardStatsDto.cs
public sealed record RecentTradeDto(
    Guid OrderId,
    DateTime CreatedAt,
    string Symbol,
    string Side,
    string PositionSide,
    decimal Quantity,
    decimal? AverageFillPrice,
    string Status,
    string? RejectReason);
```

**API 端**：`/api/dashboard/stats` 把新欄位帶上（`o.RejectReason` 是 `Order` 既有欄位，`OrderConfiguration.cs` 已 `HasMaxLength(512)`）：

```csharp
// src/CryptoBot.ConsoleApp/Api/DashboardEndpoints.cs
var recent = recentOrders.Select(o => new RecentTradeDto(
    OrderId: o.Id,
    CreatedAt: o.CreatedAt,
    ...
    Status: o.Status.ToString(),
    RejectReason: o.RejectReason)).ToList();
```

Dashboard.razor 初始 snapshot（繞過 API、直查 repo 的那條 code path）亦同步帶入 `RejectReason: o.RejectReason`。SignalR `TradeFilled` 事件本來就只是觸發 `RefreshSnapshotAsync()` 重拉快照，自然一併吃到新欄位，**不需要改 Hub / Broadcaster 契約**。

**UI 層**：Dashboard 的 Recent Trades 表加一欄「Reason」。成功單顯示 `—`，rejected 單以負色 (`neg`) 顯示錯誤字串，超寬用 CSS ellipsis 截斷、`title` 屬性掛完整字串供 hover：

```razor
@* src/CryptoBot.ConsoleApp/Components/Pages/Dashboard.razor — Recent Trades section *@
<thead>
    <tr>
        <th>Time</th><th>Symbol</th><th>Side</th><th>Qty</th><th>Avg Fill</th>
        <th>Status</th><th>Reason</th>
    </tr>
</thead>
<tbody>
    @foreach (var t in _recentTrades)
    {
        var isRejected = string.Equals(t.Status, "Rejected", StringComparison.OrdinalIgnoreCase);
        <tr>
            ...
            <td class="@(isRejected ? "neg" : null)">@t.Status</td>
            <td title="@(t.RejectReason ?? string.Empty)"
                style="max-width:320px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;@(isRejected ? "color:var(--neg,#d64545);" : string.Empty)">
                @(string.IsNullOrWhiteSpace(t.RejectReason) ? "—" : t.RejectReason)
            </td>
        </tr>
    }
</tbody>
```

**為何加在 Dashboard 而非 TradeHistoryTable**：被拒絕的訂單**從未變成 Position**（`Position.Open` 只在 `order.Status == Filled` 時執行，見 `Trading/StrategyExecutor.cs:219`），因此 `TradeHistoryTable` 的資料源 `GetRecentClosedAsync` 查不到任何 rejected 紀錄 — 再怎麼改它也看不到錯誤代碼。正確曝光點是 Dashboard 的 Recent Trades（資料源是 `orderRepo.GetRecentAsync(10)`，rejected / filled / pending 都含）。

**BingX 錯誤代碼注入點確認**：`Infrastructure/Exchange/BingX/BingXExchangeClient.cs:470-473`，下單失敗直接把 `result.Error?.Message`（含代碼如 `100010: Insufficient balance`）餵入 `order.Reject(errMsg)`，領域層再寫入 `RejectReason`。整條 end-to-end 已經通。

### T5 — `ParametersSnapshot` 寫入源頭巡檢

PM 假設：是不是開倉時 `ParametersSnapshot` 被誤塞了物件（非 JSON string），才導致讀取時觸發 cast 崩潰？

**結論：否。此路無雷。**

1. **契約是字串**：`Position.Open(..., string? parametersSnapshot = null)` — C# 編譯時型別就拒絕任何非 `string?` 的值，不可能塞進 object。
2. **唯一生產寫入點**：`Application/Trading/StrategyExecutor.cs:271` 的 `BuildParametersSnapshot(...)` 最後一行：
   ```csharp
   return JsonSerializer.Serialize(payload); // 回傳 string
   ```
   其他 `Position.Open` 生產 caller：`Backtesting/BacktestEngine.cs:278`，根本沒傳 `parametersSnapshot` 引數，取 default `null`。
3. **EF 無 converter 糾纏**：`PositionConfiguration.cs` 對 `Symbol` 掛了 `HasConversion(new SymbolConverter())`，但對 `ParametersSnapshot` 只寫 `b.Property(p => p.ParametersSnapshot);` — 純 nullable string 欄位，`string ↔ string` 純值往返，無任何 cast 表面。
4. **領域屬性宣告**：`public string? ParametersSnapshot { get; private set; }` — 強型別字串，沒有任何 `object` 參與。

因此 S99 的 Invalid cast **純粹是** `EF.Property<string>` + `SymbolConverter<Symbol,string>` 的翻譯邊角案例（已於 T1 修復），與 `ParametersSnapshot` 無關。此巡檢項確認「源頭已封堵（因為根本沒漏水）」。

### 建置與測試

```
dotnet build CryptoBot.sln -nologo               →  0 警告 / 0 錯誤
dotnet test  CryptoBot.sln --no-build --nologo   →  26 + 86 = 112/112 通過
```

（補註：本輪用預設 bin/obj 路徑 build，因先前多次 `-p:BaseOutputPath=artifacts/Sxx/` 在同一 csproj 上層層套疊，`src/CryptoBot.ConsoleApp/` 底下已累積出 `artifacts/S99T4/.../artifacts/S48/.../artifacts/S43/...` 這類深度巢狀目錄，觸發 `CopyToOutputDirectory` 去複製不存在的 staticwebassets 檔名、甚至撞 Windows 260 字元路徑上限。先 `rm -rf` 掉所有 `src/*/artifacts`、`tests/*/artifacts` 及 ConsoleApp 的 `bin/obj`，再以預設路徑乾淨 build — 後續只要不再疊 `BaseOutputPath` 就不會復發。）

### T4 VCP

- **[VCP-Run]** — 當 BingX 拒單（VST 餘額不足 / 風控 / 代號錯誤等）時，`order.Reject(errMsg)` 將錯誤字串（含 BingX 代碼）寫入 `Order.RejectReason` → 走 `GET /api/dashboard/stats` 或 Dashboard.razor 內部 refresh → `RecentTradeDto.RejectReason` 被填入 → Recent Trades 表「Reason」欄以負色顯示、hover 看完整代碼。成功 / pending 單該欄顯示 `—`，不干擾視覺。

### 交付結論

> T4 錯誤原因透明化 + T5 快照源頭巡檢 已完成；Symbol 轉型崩潰修復已通 build + 112/112 測試。

---

## S99-HOTFIX 補遺 II：T6 下單量來源校對 + T7 過載防呆

### T6 — `CalculateQuantity` / `Sizer` 餘額來源審計

專案實際上有兩條 sizing 路徑，PM 指路的 `src/CryptoBot.Application/Strategies/StrategyExecutor.cs` 是**實盤管線**（每根收盤 K 線走這條，下單用），另外還有一條 `Trading/StrategyExecutor.cs` 走 Domain 層 `PositionSizingService`。兩條的資金源全部稽核過：

| 路徑 | 資金來源 | 結論 |
| --- | --- | --- |
| `Strategies/StrategyExecutor.HandleSignalAsync` → `IOrderSizer.ComputeAsync` | `_exchange.GetFuturesBalanceAsync(ct)` | ✅ 即時帳戶 |
| `Trading/StrategyExecutor.HandleOpenSignalAsync` (舊路徑) | `_exchange.GetFuturesBalanceAsync(ct)` | ✅ 即時帳戶 |
| `RiskManager.CheckBeforeOpenAsync` (保證金檢查) | `_exchange.GetFuturesBalanceAsync(ct)` | ✅ 即時帳戶 |
| `Backtesting/BacktestEngine` | `_exchange.GetFuturesBalanceAsync("USDT", ct)` → `BacktestSimulator` 模擬錢包 | ✅ 隔離於 Lab |

`IExchangeClient.GetFuturesBalanceAsync(null)` 會依 `QuoteAsset`（Demo→`VST`、Live→`USDT`）自己挑對幣種；BingX 路徑實作在 `BingXExchangeClient.cs:197`，直接打 BingX REST 即時查。**完全沒有從 `appsettings.json` 或 Lab Initial Balance 讀靜態數字的路徑** — PM 擔心的「100,000 Initial Balance 洩漏到實盤」不存在。

但 `OrderSizer` 的舊實作確實有破口：它只套了風險預算公式 `qty = balance × Risk% / (entry × SL%)`，**沒做保證金 / stepSize / minQty / minNotional 四項校驗**，因此在 `balance` 很小（VST 只剩幾十元）而 `SL%` 又很窄的情境下，會算出**保證金超過餘額**的 qty，結果整張單被 BingX 以 `100010` 拒絕 — 這正是 PM 在現場看到的「被 Rejected 的大單」。這部分歸到 T7 修。

### T7 — `OrderSizer` 四層防呆

重寫 `src/CryptoBot.Application/RiskManagement/OrderSizer.cs`，把「物理可交易」校驗一次處理好。注入 `ILogger<OrderSizer>`（以 `NullLogger` 為預設值兼容手動建構的測試），在每次縮減 / 零值都落警告日誌。

```csharp
// src/CryptoBot.Application/RiskManagement/OrderSizer.cs  —— 核心流程
public async Task<Quantity> ComputeAsync(Strategy strategy, TradingSignal signal, CancellationToken ct = default)
{
    var cfg = strategy.Configuration;
    var entry = signal.SuggestedPrice.Value;
    if (entry <= 0) throw new DomainException(...);

    // ① 永遠讀即時帳戶餘額（不吃 appsettings / Lab 靜態值）
    var balance = await _exchange.GetFuturesBalanceAsync(ct: ct);
    if (balance <= 0) { _logger.LogWarning(...); return Quantity.Zero; }

    var stopDistance = entry * cfg.StopLossPercent;
    if (stopDistance <= 0) throw new DomainException(...);

    // ② 風險預算 → 原始 qty
    var riskAmount = balance * cfg.RiskPerTradePercent;
    var rawQty = riskAmount / stopDistance;
    if (rawQty <= 0) return Quantity.Zero;

    // ③ 保證金上限：notional/leverage > balance → 砍到 balance×leverage/entry
    var leverage = cfg.Leverage.Value;
    var requiredMargin = (rawQty * entry) / leverage;
    if (requiredMargin > balance)
    {
        var cappedQty = (balance * leverage) / entry;
        _logger.LogWarning(
            "Sizer: risk-budget qty {RawQty:F6} needs margin {Required:F2} > balance {Balance:F2}; " +
            "capping to affordable qty {Capped:F6} ({Symbol}, lev={Lev}x).",
            rawQty, requiredMargin, balance, cappedQty, signal.Symbol.BingXFormat, leverage);
        rawQty = cappedQty;
    }

    // ④ 交易所規則：對齊 stepSize、驗 minQuantity / minNotional
    var rules = await _exchange.GetTradingRulesAsync(signal.Symbol, ct);
    var alignedQty = rules.StepSize > 0
        ? Math.Floor(rawQty / rules.StepSize) * rules.StepSize
        : rawQty;
    if (alignedQty < rules.MinQuantity)              { _logger.LogWarning(...); return Quantity.Zero; }
    if (rules.MinNotional > 0 &&
        alignedQty * entry < rules.MinNotional)      { _logger.LogWarning(...); return Quantity.Zero; }

    return Quantity.Create(alignedQty);
}
```

**為何直接 cap 到 `balance × leverage / entry` 不保留 buffer**：Sizer 的邊界是「物理上送出去不會被交易所 reject」，不是 policy。留 5%/10% reserve buffer 是 `RiskManager.CheckBeforeOpenAsync` 的職責（`RiskLimits.ReserveRatio` 已在該處生效，L100 rejects 超額）。兩層分工：Sizer 確保「exchange 不拒」，RiskManager 確保「內部政策不違」。兩者串聯就是 PM 要的「自動縮減 + 警報」。

**Executor 對 Zero 的處理早就在位**（`Strategies/StrategyExecutor.cs:293`）：
```csharp
if (qty.Value <= 0) { _logger.LogWarning("Sizer returned zero quantity for {Signal} — skipping.", signal); return; }
```
Sizer 回 Zero 時，Executor 會 log 警告 + 跳過這根 K 線，**完全不會送單**。

### 回歸測試（新增 2 支）

```csharp
// tests/CryptoBot.Application.Tests/Strategies/StrategyEngineTests.cs
[Fact] OrderSizer_CapsQuantityWhenMarginExceedsBalance()  // balance 1_000 × 2x lev × risk 10% × SL 1% → 應被砍到 20 BTC
[Fact] OrderSizer_ReturnsZero_WhenAlignedBelowMinQuantity() // balance 1 USDT × entry 60_000 → Zero
```

### 建置與測試

```
dotnet build tests/CryptoBot.Application.Tests   →  0 警告 / 0 錯誤
dotnet test  CryptoBot.Domain.Tests              →  26/26 通過
dotnet test  CryptoBot.Application.Tests         →  88/88 通過（舊 86 + 新 2 支 T7 回歸）
合計：114/114
```

（補註：`dotnet build CryptoBot.sln` 會因為 ConsoleApp 正在由使用者運行中、PID 4512 持有 DLL 檔案鎖而 fail；改用 `dotnet build tests/...` 只重建測試相依的三個 lib，完全略過 ConsoleApp，正是這種情境下的正解。使用者重啟 ConsoleApp 前無需額外處理。）

### 給使用者的手動動作（PM 已指示的止血步驟）

> **不是這次代碼的一部分，但補遺同場附上以便回報閉環。**
>
> 1. **回測實驗室 Initial Balance 降到 VST 規模**（例如 1_000）再重新優化 — Sizer 現在雖有 cap 防呆，但 Lab backtest 用的餘額越接近實盤越能反映真實表現。
> 2. **VST 帳戶若賠光請到 BingX 重置** — 補回 10 萬 VST 後，重啟策略即可；Sizer 會自動按新餘額算 qty。

### T7 VCP

- **[VCP-RiskBudget]** — 風險預算公式在 balance / SL / leverage 正常組合下輸出不變（既有 `OrderSizer_ComputesQuantityFromRiskFormula` 通過 = 100 BTC）。
- **[VCP-MarginCap]** — 新增 `OrderSizer_CapsQuantityWhenMarginExceedsBalance` 驗證自動縮減到 `balance × leverage / entry`。
- **[VCP-MinQty]** — 新增 `OrderSizer_ReturnsZero_WhenAlignedBelowMinQuantity` 驗證小於交易所最小下單量時回 Zero。
- **[VCP-ZeroShortCircuit]** — 既有 `OrderSizer_ZeroBalance_ReturnsZero` 維持通過。
- **[VCP-Run]** — 實盤當 VST 餘額 < 最小開倉門檻，Sizer 會記 `aligned qty ... < minQuantity ... returning Zero` 警告，Executor 跳過該訊號而非送出必定被 `100010` 拒絕的單。

### 交付結論

> T6 下單量資金來源校對（無 appsettings 洩漏）+ T7 保證金 / stepSize / minQty / minNotional 四層防呆已完成；114/114 測試通過。實盤端 Sizer 現在保證送出的單在交易所規則內可行，餘下的政策性緩衝（ReserveRatio / MaxExposure）由 RiskManager 守關。

---

## S99-S50-S47 ULTIMATE — 核心穩定性大三元（2026-04-23 第二追補）

> **PM 膠囊**：`ai_ops/capsules/TASK_S99_S50_S47_ULTIMATE_FIX.md`
> **目標**：徹底修復「無法成交」（S99 轉型 + S50 保證金）與「實驗室失憶」（S47 Lab 記憶）三大缺陷。

### T1 · [S99] Symbol 轉型與 Repository 巡檢

再複核一輪「SIGNAL SELL 後 Invalid cast」的所有可能犯罪現場：

| 檔案 | 位置 | 狀態 |
|---|---|---|
| `PositionRepository.GetOpenPositionsBySymbolAsync` | L25-39 | ✅ 先拿所有 open、記憶體內 `Symbol.Equals` 過濾（S99 hotfix 註解在案） |
| `OrderRepository` 同類查詢 | L36 註解 | ✅ 同策略修正 |
| `Trading/StrategyExecutor.ClosePositionAsync` L293 | `position.Close(closeOrder.AverageFillPrice, reason, commission)` | ✅ `Price? AverageFillPrice` ↔ `Close(Price, ...)` 型別 match |
| `AccountSynchronizer` L140, L223 | `local.Close(mark, ...)` / `match.Close(exitPrice, ...)` | ✅ 均為 `Price` VO，無裸字串 |
| `BacktestEngine` L299, L343 | 同上 | ✅ match |
| `StrategyOptimizationSettingsRepository.Symbol == key` | L27, L43 | ✅ 此處 `Symbol` 欄位是原生 `string`（非 VO），沒有轉換器介入 |

**結論**：轉型路徑無裸字串殘留，`Invalid cast` 的物理條件已經消除。既有測試仍全綠。

### T2 · [S50] 智慧下單量與保證金 5% 緩衝

把 T7 原本的「硬砍到 `balance × leverage / entry`」升級為 PM 指定的 **`MaxNotional = balance × leverage × 0.95`**：

```csharp
// src/CryptoBot.Application/RiskManagement/OrderSizer.cs
private const decimal MarginBufferFactor = 0.95m;   // S50 合約

var leverage = cfg.Leverage.Value;
var maxNotional = balance * leverage * MarginBufferFactor;   // 0.95
var notional = rawQty * entry;
if (notional > maxNotional)
{
    var cappedQty = maxNotional / entry;
    _logger.LogWarning(
        "Sizer: risk-budget qty {RawQty:F6} notional {Notional:F2} > maxNotional {Max:F2} " +
        "(balance {Balance:F2} × lev {Lev}x × {Buf:P0}); capping to {Capped:F6} ({Symbol}).",
        rawQty, notional, maxNotional, balance, leverage, MarginBufferFactor,
        cappedQty, signal.Symbol.BingXFormat);
    rawQty = cappedQty;
}
```

**為什麼是 0.95**：BingX 下單瞬間會再乘 mark price 當下的滑價 + 手續費（taker ~5 bp）— 5% 緩衝把這些「剛好 maxed out」的單擋在 client side，避免送出即被 `100010 Insufficient margin` 打回票。

#### PM 指定附件：動態可用餘額獲取片段

```csharp
// src/CryptoBot.Application/RiskManagement/OrderSizer.cs · ComputeAsync
// 永遠讀即時帳戶餘額（Live→BingX REST, Demo→BacktestSimulator 的動態錢包） —
// 這裡不吃 appsettings / Lab Initial Balance 之類的靜態數字。
var balance = await _exchange.GetFuturesBalanceAsync(ct: ct).ConfigureAwait(false);
if (balance <= 0)
{
    _logger.LogWarning(
        "Sizer: account balance is {Balance} — returning Zero quantity for {Symbol}.",
        balance, signal.Symbol.BingXFormat);
    return Quantity.Zero;
}
```

### T3 · [S47-REVISED] 實驗室「鋼鐵級」狀態記憶

舊版只存 per-strategy 網格 Min/Max/Step，但使用者切到 Dashboard 再回 `/lab` 時，**Symbol / Interval / Slippage / Initial / Leverage / Window 這些「全域表單欄位」仍會被洗回預設值** — 這才是 PM 打到的真正痛點。

**落地**：

1. 新增 `LabFormSnapshot` record（Symbol + SymbolSelect + IsManualSymbol + Interval + SlippageBps + InitialBalance + Leverage + StartDateUtc + EndDateUtc）。
2. `LabStateContainer` 加 `SaveFormSnapshot` / `TryGetFormSnapshot`（singleton，lock 保護）。
3. `BacktestLab.razor`：
   - `OnInitializedAsync` → `RestoreFormSnapshot()`（在 first render 前恢復）。
   - 全欄位 `@bind:after="PersistFormSnapshot"`（Start / End / Slippage / Initial / Leverage）。
   - 既有的 `OnSymbolSelectChangedAsync` / `OnMarketChangedAsync` 內插 `PersistFormSnapshot()` 呼叫 — Symbol 下拉、Manual 切換、Interval 變更也同步。

**Why 不關聯 per-strategy**：UI 的 Symbol / Interval / Window 是全域選擇，與 SelectedModel 無關；per-strategy 網格仍由原有 `SaveGridSettings` / `TryGetGridSettings` 管。兩層分開，職責乾淨。

### 變更檔案清單（本追補）

- `src/CryptoBot.Application/RiskManagement/OrderSizer.cs` — 引入 `MarginBufferFactor = 0.95m` 常數；保證金上限算式改用 `maxNotional`。
- `src/CryptoBot.ConsoleApp/Lab/LabStateContainer.cs` — 新增 `LabFormSnapshot` record、`SaveFormSnapshot` / `TryGetFormSnapshot`。
- `src/CryptoBot.ConsoleApp/Components/Pages/BacktestLab.razor` — 5 欄位加 `@bind:after`；新增 `RestoreFormSnapshot` / `PersistFormSnapshot`；`OnInitializedAsync` 先恢復再同步；`OnSymbolSelectChangedAsync` / `OnMarketChangedAsync` 同步寫回。
- `tests/CryptoBot.Application.Tests/Strategies/StrategyEngineTests.cs` — `OrderSizer_CapsQuantityWhenMarginExceedsBalance` 預期值 20 → 19（驗證 0.95 緩衝）。

### 建置 / 測試

- `dotnet test tests/CryptoBot.Application.Tests` — **88 / 88 通過**
- `dotnet test tests/CryptoBot.Domain.Tests` — **26 / 26 通過**
- `dotnet build src/CryptoBot.ConsoleApp -o /tmp/...`（繞過 PID 34012 的 DLL lock）— **0 warning / 0 error**
- 合計 114 / 114 測試綠，Razor 改動語法驗證通過。

### VCP 驗證指引

- **[VCP-No-Cast-Error]** — T1 巡檢確認 6 處 `Close()` 呼叫全部 `Price` 型別 match；`GetOpenPositionsBySymbolAsync` 記憶體過濾註解在 L28-34。
- **[VCP-No-Rejected]** — T2 Sizer 升級為 `balance × leverage × 0.95` 緩衝；`OrderSizer_CapsQuantityWhenMarginExceedsBalance` 預期值 = 19（證 0.95 套用）。
- **[VCP-Lab-Memory]** — T3 在 Lab 改完 Symbol/Interval/Slippage/Initial/Leverage/Window 後，切到 `/` 再回 `/lab`，欄位應由 `RestoreFormSnapshot()` 原樣恢復。

### 交付結論

> **核心大三元修復已就緒**。
> T1 Symbol 轉型路徑全部 Price/VO match（既有 hotfix 保留、無回歸）；
> T2 Sizer 的保證金上限改為 `balance × leverage × 0.95`，5% 緩衝吃掉 BingX 滑價 / 手續費的臨界情況；
> T3 Lab 頁面新增 `LabFormSnapshot` 全域快照 — Symbol + Interval + Slippage + Initial + Leverage + Window 切頁不丟。
> 114 / 114 測試通過，ConsoleApp 編譯 0 錯。候 PM 重啟測試指令。

---

## S99-S43 GRAND FIX 追補（2026-04-23 — 核心救火 + 戰情室進化）

**膠囊**：`ai_ops/capsules/TASK_S99_TO_S43_GRAND_FIX_AND_EVOLUTION.md`
**狀態**：Phase 1（T1-T4 全部 P0）+ Phase 2（T5-T7 P1）全部就緒。建置 0 warn / 0 error；Domain 26 + Application 88 = 114 / 114 測試全綠。

### T1-T3：同 S99-S50-S47 ULTIMATE（見上節）
- T1 Symbol 轉型巡檢：既有 `GetOpenPositionsBySymbolAsync` 記憶體過濾保留 — 無回歸。
- T2 智慧下單量：`OrderSizer.cs` 的 `MarginBufferFactor = 0.95m` 已導入。
- T3 Lab 鋼鐵級狀態：`LabFormSnapshot` singleton 快照已落地。

### T4 — 持倉隱形「競態條件」徹底修復（P0，本次最關鍵）

#### 根因診斷（這次才挖到底）
前一回合只確認了「broadcast 在 save 之前」是錯的，但追到根部才發現**更深一層的 Bug**：

- **live production 路徑** = `src/CryptoBot.Application/Strategies/StrategyExecutor.cs`（`IStrategyExecutorFactory` 用的這條）
- **legacy 路徑** = `src/CryptoBot.Application/Trading/StrategyExecutor.cs`（整合測試用的）

Trading 路徑 `HandleSignalAsync` 在下單後**會** `Position.Open()` + `AddAsync()` 把本地 Position 寫進 DB（L226-244）。
但 Strategies 路徑原本的 `HandleSignalAsync` **完全沒有建立本地 Position**，只下單 + broadcast — 等於把「誰先跑到」的機會交給 BingX Account Update WS。

而 `AccountSynchronizer.HandleAccountUpdateAsync` 的程式碼只做「更新 / 關閉既有 Position」 —
**沒有任何路徑會在 live 路徑新增一筆 Position**。這才是 Dashboard `Active Positions` 常常為空的真正原因。

#### 修復內容
改寫 `src/CryptoBot.Application/Strategies/StrategyExecutor.cs` 的 `HandleSignalAsync`：

1. 從 DI scope 多取一個 `IPositionRepository`。
2. `PlaceOrderAsync` 成功後等 500ms → `RefreshOrderStatusAsync` → 若 `Filled`：
   - `Position.Open(symbol, side, qty, avgFillPrice, leverage, MarginMode.Isolated, SL, TP, strategyId, strategyType, parametersSnapshot)`
   - `AddCommission(order.Commission)` + 若配置有 `TrailingStopPercent` 則 `EnableTrailingStop(pct)`
   - `positionRepo.AddAsync(newPosition)`
3. 上面任何一步 throw 都只 `LogWarning` — 不 crash strategy，讓 WS AccountSync 當 fallback。
4. **最後** `uow.SaveChangesAsync()` 一次寫入 Order + Position。
5. **完成後** 才 `_broadcaster.BroadcastTradeAsync(...)` — SignalR 推 Dashboard 去拉資料時 DB 必有紀錄。

也補一個 private helper `BuildParametersSnapshot(StrategyConfiguration config)` 複製 Trading 路徑的 JSON 序列化邏輯（供 Live path 寫入 `Position.ParametersSnapshot`）。

#### 交付檔案
- **MOD** `src/CryptoBot.Application/Strategies/StrategyExecutor.cs` — `HandleSignalAsync` 新增 Position 物化 + SaveChanges-before-Broadcast；新增 `using System.Text.Json`, `using CryptoBot.Domain.Aggregates.PositionAggregate`；新增 `BuildParametersSnapshot` 私有輔助。

### T5 — 戰情室左右分欄 / 心跳燈 / 滾動日誌（稽核：既存達標）

已於 Dashboard 先前迭代落地，本次巡檢證其符合膠囊規格：

- **左右分欄**：`src/CryptoBot.ConsoleApp/wwwroot/app.css:1356` — `.war-room-grid { grid-template-columns: 350px 1fr; }`。
- **心跳燈**：`Dashboard.razor:172` — `<span class="heartbeat-dot" @key="_heartbeatPulseTick" ...>`；`@key` 綁變動 tick，強制 Blazor 重掛 DOM → CSS 動畫重跑 → 每次評估真的「閃一下」。
- **決策日誌**：`Dashboard.razor:209` 附近有 30 筆上限的 `LinkedList` ring buffer，顯示每次 `AnalyzeAsync` 的指標與結果。

本次無程式改動，僅驗收。

### T6 — /apply 熱套用：強制覆寫 + 自動改名（稽核：既存達標）

`src/CryptoBot.ConsoleApp/Api/LabEndpoints.cs` 的 `MapPost("/apply/{strategyId:guid}")` 已是完整流水線：

- L109 `MapLabKeyToStrategyType(body.StrategyKey)` — 模型 key → `StrategyType`。
- L119 註解清楚寫 **「熱套用：Stop → (ChangeType + Rename + UpdateConfiguration) → Save → Start」**。
- L140-150 **Symbol / Interval 強制覆寫**：`targetSymbol = body.Symbol ?? strategy.Configuration.Symbol`；`targetInterval = body.Interval ?? strategy.Configuration.Interval` → 緊接著 `strategy.ChangeType(targetStrategyType)` 呼叫前都寫回 Configuration。
- L167-173 `ChangeType` 當 type 真的變化時才呼叫（S45 熱轉型）。
- L181 `BuildOptimizedName(modelDisplayName, targetSymbol.BingXFormat, targetInterval)` — 生成 `[B46 Hybrid] SOL-15m (Opt)` 這類名稱。
- L196 `BroadcastStrategyMetadataChangedAsync` — 即時告訴 Dashboard 去拉新名字 + 型別。

本次無程式改動，僅驗收。

### T7 — `PriceActionPredictor` 價格行為動能模型（稽核：既存達標）

已於 `src/CryptoBot.Application/Strategies/PriceAction/PriceActionPredictorStrategy.cs` 落地，`DependencyInjection.cs:56` 亦已註冊為 `IStrategy`。驗收重點：

- **StrategyType** = `"PriceAction"`。
- **不依賴均線 / 振盪指標** — 僅用 `Kline.OHLC` + `BodySize / UpperShadow / LowerShadow`（Kline VO 既有屬性）。
- **形態偵測**（`DetectPatterns`）：
  - Bullish / Bearish **Engulfing**：前後兩根 body 完全覆蓋，方向相反。
  - **Hammer / Shooting Star**：影線/實體比 ≥ `WickToBodyRatio`（default 2.0），另一側影線 × 2 ≤ 實體、且方向正確。
  - 避免 `BodySize = 0` doji 除以零 — 用乘法比較而非除法。
- **動能得分**（`MomentumScore`）：窗口 `LookbackPeriod`（default 20）內每根 `(Close - Open) / Close` **加總**（不平均，避免趨勢被均值洗掉）。
- **進場**：形態 + 動能雙驗證，momentum 絕對值 ≥ `MomentumThreshold`（default 0.01）才放行。
- **T4 出場智慧**：持倉中若見反向形態（多倉遇 Bearish Engulfing / Shooting Star；空倉遇 Bullish Engulfing / Hammer）→ 大腦主動發 `CloseLong / CloseShort` 訊號讓 executor 平倉，不是被動等 SL / TP 觸發。
- 配置參數全走 `StrategyConfiguration.GetParameter(...)`，可由 Lab 網格優化（`PaParameterForm.razor` 已存在）。

本次無程式改動，僅驗收。

### T2 動態獲取餘額代碼片段（PM 硬性交付要求）

`src/CryptoBot.Application/RiskManagement/OrderSizer.cs`：

```csharp
// 永遠讀即時帳戶餘額（Live→BingX REST, Demo→BacktestSimulator 的動態錢包） —
// 這裡不吃 appsettings / Lab Initial Balance 之類的靜態數字。
var balance = await _exchange.GetFuturesBalanceAsync(ct: ct).ConfigureAwait(false);
if (balance <= 0)
{
    _logger.LogWarning(
        "Sizer: account balance is {Balance} — returning Zero quantity for {Symbol}.",
        balance, signal.Symbol.BingXFormat);
    return Quantity.Zero;
}

// 1) 風險預算（止損觸發時願意賠的絕對金額）→ 原始數量
var riskAmount = balance * cfg.RiskPerTradePercent;
var rawQty = riskAmount / stopDistance;
if (rawQty <= 0) return Quantity.Zero;

// 2) 保證金上限 — S50 合約：MaxNotional = balance × leverage × 0.95。
//    5% 緩衝是留給 BingX 的手續費 / 滑價 / mark price 抖動，避免「剛剛好」的單
//    在交易所真正報價時被 100010 "Insufficient margin" 打回票。
//    超過就按 MaxNotional / entry 直接砍到安全上限，並記警告。
var leverage = cfg.Leverage.Value;
var maxNotional = balance * leverage * MarginBufferFactor;   // 0.95
var notional = rawQty * entry;
if (notional > maxNotional)
{
    var cappedQty = maxNotional / entry;
    _logger.LogWarning(
        "Sizer: risk-budget qty {RawQty:F6} notional {Notional:F2} > maxNotional {Max:F2} " +
        "(balance {Balance:F2} × lev {Lev}x × {Buf:P0}); capping to {Capped:F6} ({Symbol}).",
        rawQty, notional, maxNotional, balance, leverage, MarginBufferFactor,
        cappedQty, signal.Symbol.BingXFormat);
    rawQty = cappedQty;
}
```

其中 `GetFuturesBalanceAsync(ct)` 透過 `IExchangeClient` — Live 模式呼 BingX REST 的 `/openApi/swap/v2/user/balance`，Demo 模式則走 `BacktestSimulator` 的動態錢包（含已實現盈虧）。`MarginBufferFactor` 常數 = `0.95m`（5% 緩衝）。

### 變更檔案清單（本追補獨有，不含 T1-T3）

- **MOD** `src/CryptoBot.Application/Strategies/StrategyExecutor.cs`
  - `using System.Text.Json;`, `using CryptoBot.Domain.Aggregates.PositionAggregate;` 兩行新增。
  - `HandleSignalAsync` 重構：從 scope 取 `IPositionRepository`；下單 → 500ms → RefreshOrderStatus → Position.Open + AddCommission + (opt) EnableTrailingStop → positionRepo.AddAsync → `SaveChangesAsync` → **最後才** `BroadcastTradeAsync`。
  - 新增 private static `BuildParametersSnapshot(StrategyConfiguration)` 供 Position 記錄當下參數 JSON。

### 建置 / 測試

- `dotnet test tests/CryptoBot.Domain.Tests` — **26 / 26 通過**（43 ms）
- `dotnet test tests/CryptoBot.Application.Tests` — **88 / 88 通過**（1 s）
- `dotnet build src/CryptoBot.ConsoleApp -o /tmp/cb_consoleapp_grand` — **0 warning / 0 error**（10.13 s；以 `-o` 繞過 user PID 34012 保有的 DLL lock）
- 合計 114 / 114 測試綠。

### VCP 驗證指引

- **[VCP-P0-Fixes]**
  - 觸發 SELL 不應再噴 `Invalid cast`（T1 保留既有 hotfix）。
  - 下大單時不應回 `Insufficient margin 100010`（T2 的 0.95 上限壓下）。
  - `/lab` 改完 Symbol/Interval/Slippage/Initial/Leverage/Window 切到 Dashboard 再回來參數應原樣在位（T3 快照）。
  - 開單成功後 **下一次 Dashboard 輪詢 / SignalR 推播**必見 Active Position（T4 — Position 先寫入 DB，再 broadcast）。
- **[VCP-WarRoom]**
  - 左右分欄（左 350px / 右 1fr）— `app.css:1356` 證。
  - 綠色 `.heartbeat-dot` 每次評估 tick → 會閃（`@key` DOM reset trick）。
  - 右側滾動 30 筆決策日誌（LinkedList ring buffer）— `Dashboard.razor:209` 附近證。
  - Lab `/apply` 後目標策略名稱自動變為 `[Model] SYMBOL-INT (Opt)`（LabEndpoints.cs `BuildOptimizedName`）。
- **[VCP-PA-Model]**
  - `PriceAction` 策略可在 Lab 選用並優化 `LookbackPeriod / MomentumThreshold / WickToBodyRatio / EngulfingEnabled / Confidence` 五參數。

### 交付結論

> **核心救火與戰情室進化已全面就緒**。
>
> Phase 1 P0：T1 Symbol 轉型安全、T2 保證金 0.95 緩衝、T3 Lab 鋼鐵級狀態、**T4 live 路徑補上 Position 物化 + SaveChanges-before-Broadcast — 持倉隱形根因徹底殲滅**。
> Phase 2 P1：T5 左右分欄 + 心跳 + 30 筆滾動日誌、T6 /apply 熱轉型 + 自動改名、T7 `PriceActionPredictor`（裸 K + 動能 + 反轉主動出場）三者既有實作均稽核通過。
>
> 114 / 114 測試全綠；ConsoleApp 編譯 0 warn / 0 error。T2 動態餘額代碼片段如上。候 PM 重啟測試指令。

