# CryptoBot 開發憲章 (Development Manifesto)

> **版本**：v1.0 · 2026-04-21 · 鎖定於 Beta v0.1
> **位階**：本文件為本專案最高技術準則，凌駕個別 PR / Issue / 心情。
> 任何與本憲章衝突的程式碼、設計或流程，**一律以憲章為準**。
> 修訂憲章本身需要明確的 commit + 在本檔頂端遞增版本號。

---

## §0. 前言 — 為什麼需要憲章

CryptoBot 是一台會自己用真實資金（即便是 demo 額度）下單的引擎。

它不是 hackathon demo、不是 weekend project、不是「先動再說」的工具。它是金融機器，**錯一行可能燒一晚**。所以這份憲章存在的目的，是把所有「以後可能會吃虧」的決策，提前固化成規矩。

當你（未來的我、或新進的 Claude session）打開這個 repo 想動程式時，請先讀完這份文件。

---

## §1. Clean Architecture 規範

### 1.1 四層相依方向（**唯一合法方向**）

```
ConsoleApp  ──▶  Application  ──▶  Domain
       │                 │             ▲
       └─▶ Infrastructure ─────────────┘
              （只實作 Domain/Application 定義的介面）
```

- **Domain** — 純 C#，零外部相依（除 BCL）。
- **Application** — 編排業務流程，定義介面（Repository、ExchangeClient、HistoricalKlineStore…），**不知道任何實作細節**。
- **Infrastructure** — 把 EF Core / BingX SDK / SignalR / SQLite 等外部世界翻譯成 Application 認得的介面。
- **ConsoleApp** — 組合根（Composition Root）。負責 DI 註冊、Web host、Blazor、Minimal API、SignalR Hub。

### 1.2 鐵律（**絕對禁止**）

| # | 禁令 | 違反後果 |
|---|---|---|
| 1 | `Domain` 引用 `Application` / `Infrastructure` / `ConsoleApp` | Domain 失去純粹性，Domain.Tests 開始要 mock 一堆東西 |
| 2 | `Application` 直接 `new EntityFrameworkXxx()`、直接 `new BingXClient()` | 跨層耦合，replay / backtest / mock 全部炸 |
| 3 | `Domain` / `Application` 出現 `Microsoft.EntityFrameworkCore.*`、`BingX.Net.*`、`Microsoft.AspNetCore.*` 的 `using` | 同上 |
| 4 | `Infrastructure` 把 EF / BingX 的型別「漏」回 Application 簽章 | 抽象洩漏，下次換 Exchange 整層拆到天亮 |
| 5 | 在 `Domain` 寫 `DateTime.UtcNow` / `Guid.NewGuid()` 等不可決定行為（除 ctor 預設值） | 單元測試非確定，Domain 不再可重放 |

### 1.3 介面歸屬

- 「**Application 需要的能力**」→ 介面定義在 Application（例：`IStrategyExecutor`、`IHistoricalKlineStore`）。
- 「**Domain 需要的能力**」→ 介面定義在 Domain（例：`IStrategyRepository`、`IOrderRepository`）。
- Infrastructure 只能往 Domain/Application 看；**永遠不要在 Domain 為了 Infrastructure 開插槽**。

### 1.4 例外條款 — 交易所專屬功能

Binance / BingX 系列的 ListenKey、user-data WS 訂閱等屬於 BingX **獨有**，不在 `IExchangeClient` 上。
這類方法定義在 `BingXExchangeClient` 具體型別上，DI 必須**同時**註冊成 `IExchangeClient` 與具體型別。

> **理由**：保護 Application 對「交易所」的抽象 — 別讓 Binance/BingX 的特殊性污染未來 OKX/Bybit 的接入。

---

## §2. 策略插槽協議 (Strategy Slot Protocol)

新增「決策大腦」是 CryptoBot 最高頻的演進動作。整個流程**必須走以下三步 SOP**，缺一不可。

### Step 1 — 實作 `IStrategy`（位於 `CryptoBot.Application/Strategies/<Name>/`)

```csharp
public sealed class MyStrategy : IStrategy
{
    public string StrategyType => "MyStrategy";

    public Task<TradingSignal> AnalyzeAsync(
        StrategyConfiguration config,
        IReadOnlyList<Kline> klines,
        MarketSnapshot snapshot,
        IReadOnlyList<Position> openPositions,
        CancellationToken ct = default)
    {
        // 純函數運算 — 拿到的所有東西都是 Domain 物件，不准呼叫 IO
        var fast = (int)config.GetParameter("FastPeriod");
        // ...
        return Task.FromResult(TradingSignal.Hold(...));
    }
}
```

**約束**：
- `AnalyzeAsync` 必須是純函式 — 同樣的輸入 → 同樣的輸出。
- **嚴禁** 在策略內呼叫 HTTP、DB、log 持久化。要看資料就從參數拿。
- 參數從 `config.Parameters` 讀，型別永遠是 `decimal`，要轉 int 就 `(int)`。

### Step 2 — 建立繼承 `StrategyParameterFormBase` 的 UI

位置：`CryptoBot.ConsoleApp/Components/Lab/<Name>ParameterForm.razor`

```razor
@namespace CryptoBot.ConsoleApp.Components.Lab
@inherits CryptoBot.ConsoleApp.Lab.StrategyParameterFormBase
@using CryptoBot.ConsoleApp.Services

<div class="glass-form">
    <!-- 你的 input 區塊 -->
</div>

@code {
    // 1) 暴露 CurrentGridSize：笛卡兒積總組數，給狀態艙顯示 grid size
    public override int CurrentGridSize => /* ... */;

    // 2) 暴露 BuildRequest：把表單 + 時間窗轉成 OptimizationRequest
    public override OptimizationRequest? BuildRequest(DateTime startUtc, DateTime endUtc, out string? error) { ... }
}
```

**約束**：
- 任何欄位變動都要 `@bind:after="OnChanged"` 觸發 `NotifyChangedAsync()`，否則 grid size / engine load 不會跟著動。
- 驗證失敗時 `BuildRequest` 回 `null` 並設 `error`，**不要 throw**。

### Step 3 — 在 `StrategyCatalog` 註冊

```csharp
Register(new StrategyModel(
    Key: "my-strategy",
    DisplayName: "My Strategy",
    Subtitle: "一句話介紹",
    FormComponent: typeof(Components.Lab.MyParameterForm),
    IsLocked: false));
```

加完這三步，`/lab` 頁面的 Pills 列、DynamicComponent、SignalR 進度推播、Leaderboard、Apply 流程**全部自動就位** — 不需要動 `BacktestLab.razor`。

### Step 3.5 — Orchestrator 編排（如果策略需要客製回測流程）

目前 `OptimizationOrchestrator` 寫死 SMA 的網格。新策略若需要不同的 metric 或不同的組合維度，請在 `OptimizationOrchestrator` 增加 dispatch（依 `SelectedModel.Key`）— 而不是另寫一個 Orchestrator。

---

## §3. 零容忍契約 (Zero-Tolerance Contract)

CryptoBot 的 main 分支永遠維持以下不變式（invariants）。任何 PR 違反任一條，**不准 merge**。

### 3.1 編譯品質

```
dotnet build -c Debug   →  0 警告 / 0 錯誤
dotnet build -c Release →  0 警告 / 0 錯誤
```

> **不准用 `#pragma warning disable` 蓋掉** — 修到根本，不然就改設計。
> 唯一可接受的例外：第三方產生器產出的程式碼，且必須在該 disable 上一行寫明來源 & 為何不可避免。

### 3.2 測試通過率

```
dotnet test → 100% pass
```

- 加新功能必須**同步加測試**。Domain 邏輯一律走 Domain.Tests；跨層流程走 Application.Tests/Integration。
- 不准用 `[Fact(Skip = "...")]` 跳過。要嘛修綠、要嘛刪掉並在 commit 訊息說明。
- Beta v0.1 基線：**46/46**（Domain 1 + Application 45）。日後新增功能必須維持「總數只增不減」。

### 3.3 Beta 發布前自檢清單

- [ ] `dotnet build` 0/0
- [ ] `dotnet test` 100%
- [ ] `/lab` 頁面手動跑一次完整優化（最少 25 組合）→ Leaderboard 出來、Apply 成功
- [ ] Dashboard 卡片數字會跳、Recent Orders 有資料
- [ ] 重新整理頁面後 LabStateContainer 仍然顯示前次 Leaderboard

---

## §4. UI / 異步規範

### 4.1 推播路徑（**唯一合法管線**）

```
Backend Job
   │
   ▼
DashboardEventBus.RaiseXxx()         （in-process bus，Blazor 直接訂）
   │
   └─▶ IHubContext<TradeHub>.Clients.All.SendAsync(...)   （SignalR 對外推）
```

- Blazor 元件**只訂 `DashboardEventBus`**，不直接接 SignalR — 同進程沒理由繞一圈 WebSocket。
- 外部 Web client 一律走 SignalR Hub，避免 Blazor 與外部 client 兩條訊息流不一致。
- **所有事件都從 Orchestrator 觸發**。任何地方想 push 都先去 Orchestrator 拿一條 `Raise...`，不准 Blazor 直接呼叫 Hub。

### 4.2 狀態管理

- **單一狀態艙：`LabStateContainer` (Singleton)**
- Blazor 頁面**禁止**自行用 field 持有跨頁面狀態（progress、leaderboard、isRunning…），全部從 `State.Xxx` 拿。
- 跨 tab、刷新、進出 `/lab` 都不可丟資料 — 所有狀態在 Container；頁面只是 view。
- `StateChanged` event 是唯一 re-render 觸發點，元件 `Dispose` 時務必 `-= OnStateChanged`，否則記憶體洩漏 + Ghost UI。

### 4.3 異步紀律

| 場景 | 准 | 不准 |
|---|---|---|
| 等資料 | `await ...Async()` | `.Result` / `.Wait()` |
| 等狀態變化 | 訂 `StateChanged` / SignalR event | `while(...) Task.Delay(...)` 輪詢 |
| 長任務 | `Task.Run(...)` + Singleton Gate (`SemaphoreSlim(1,1).Wait(0)`) | 在 HTTP request 同步跑回測 |
| Blazor render | `InvokeAsync(StateHasChanged)` | 在背景 thread 直接 `StateHasChanged()` |
| 取消 | 接 `CancellationToken` 一路傳 | 自己造 `bool _cancelled` 旗標 |

### 4.4 卡死 UI 的零容忍清單（PR review 一律拒絕）

1. UI 執行緒上的 `Thread.Sleep` / `Task.Wait()` / `Task.Result`
2. 對 Hub 或 Container 做 polling（`while(true) await Task.Delay(500)`）
3. 在 `OnInitializedAsync` 裡 sync 等很久的 IO 沒 spinner / skeleton
4. 進度推播後沒 `InvokeAsync(StateHasChanged)`，UI 不更新

---

## §5. 命名 / 風格速查

- 檔名 = 主型別名；Razor 元件用 PascalCase。
- DI 註冊位置：所有 web 相依的單例放在 `Program.BuildApp`，**不要散在各層 Module 裡**。
- 中文註解可以寫商業邏輯與「為什麼」；技術細節 / API doc 寫英文。
- HANDOFF 鏈是工作交接的真理 — 每個 session 結束新增一份 `HANDOFF_N.md`，下個 session 從上一份讀起。

---

## §6. 修訂紀錄

| 版本 | 日期 | 變更 |
|---|---|---|
| v1.0 | 2026-04-21 | 初版鎖定於 Beta v0.1 — Clean Arch / Strategy Slot SOP / Zero Tolerance / UI 規範 |

---

> _「規矩不是用來限制創造力的，規矩是讓你不用每次都重新發明創造力。」_
