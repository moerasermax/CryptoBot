# CryptoBot · BingX .NET 10 Algorithmic Trading Engine

> **Beta v0.2** — 一台跑在 .NET 上、專為 BingX 永續合約打造的量化交易引擎，
> 內建即時策略執行、SignalR 戰情室、SMA 並行優化回測、模組化策略大腦插槽、
> 動態金鑰管理、以及儀表板策略手動控制台。
>
> _「不是 demo，是會自己賺錢（或燒錢）的引擎。」_

---

## ✨ Beta v0.2 亮點

| 模組 | 內容 |
|---|---|
| 🔑 **動態金鑰管理 (S24)** | `ExchangeAccount` Aggregate + SQLite 持久化；`/settings/exchanges` 頁面手動輸入 / 更新 / 啟用切換；首啟若無 active 金鑰 Dashboard 彈 onboarding Modal；`IExchangeCredentialProvider.CredentialsChanged` 事件讓 BingX SDK client 原子重建 |
| 🎛️ **策略手動控制台 (S25)** | Dashboard 內建「策略大腦」下拉 + Running/Stopped Toggle；型別切換走 Domain-layer `Strategy.ChangeType`（Running 時直接 throw），API 以 `IStrategyFactory.KnownTypes` 白名單驗證，HostedService `_mutateLock` 序列化 Stop→ChangeType→Rebuild Executor→Start |
| 🔁 **自動續期 ListenKey** | BingX user-data WS 強型別處理；30 分自動 PUT 續期；expired 事件交給上層 Stop→Start 復原 |
| 📊 **SMA 並行優化回測** | 笛卡兒積展開 Fast×Slow 網格，scope-per-run 並行回測，依 P/DD ratio 排名 |
| 📡 **SignalR 即時推播** | `DashboardEventBus` 雙通道：Blazor in-process + 外部 WebSocket 一致觸發 |
| 🧠 **動態策略插槽 UI** | `StrategyCatalog` + `DynamicComponent` — 加新策略只需 3 步，不必碰主頁 |
| 🪟 **玻璃擬態戰情室** | `#121212` 深色基底、Pills nav、金黃進度條、ETA、skeleton + fade-in |
| 🔒 **狀態艙 Singleton** | `LabStateContainer` 跨頁面/刷新/tab 不丟資料 |
| ♻️ **Demo↔Live 熱切換** | `IEnvironmentSwitcher` Stop-First 編排 + 二次確認 Modal + 切換後不自動重啟（強制人為再確認） |
| 🧪 **零容忍品質** | 0 Warning / 0 Error · 全測試綠 |

---

## 🧱 技術棧

| 層 | 技術 |
|---|---|
| Runtime | **.NET 8 target** on **.NET 10 preview SDK** |
| Web | ASP.NET Core · Blazor Server · Minimal APIs · SignalR |
| Persistence | EF Core · SQLite (本地零依賴) |
| Exchange SDK | BingX.Net v3.10.0（強型別 WS handlers） |
| Logging / Notify | Serilog · Discord Webhook |
| Tests | xUnit · FluentAssertions |
| Architecture | Clean Architecture + DDD（Aggregate / Value Object / Domain Service） |
| UI | Glassmorphism + Mermaid docs |

---

## 🏗️ 架構

```
┌─────────────────────────────────────────────────────────────┐
│  ConsoleApp  (Blazor + SignalR + Minimal API + DI 組合根)     │
├─────────────────────────────────────────────────────────────┤
│  Application  (IStrategy, BacktestEngine, Synchronizer …)    │
├─────────────────────────────────────────────────────────────┤
│  Domain  (Aggregates, Value Objects, Repository 介面)        │
├─────────────────────────────────────────────────────────────┤
│  Infrastructure  (BingX, EF Core, SignalR, Discord)          │
└─────────────────────────────────────────────────────────────┘
```

📐 詳細圖表：
- [`docs/architecture/System_Architecture.md`](./docs/architecture/System_Architecture.md) — 四層相依 + 元件圖
- [`docs/architecture/Data_Flow.md`](./docs/architecture/Data_Flow.md) — Live trading + Backtest sequence
- [`docs/architecture/UML_Core_Domain.md`](./docs/architecture/UML_Core_Domain.md) — Domain 類別圖 + Strategy Slot 契約

📜 [`CryptoBot_Dev_Protocol.md`](./CryptoBot_Dev_Protocol.md) — **動程式之前先讀的開發憲章**。

---

## 🚀 Quick Start

### 環境需求
- .NET 10 preview SDK（target: net8.0；用 preview SDK 才不會建置警告）
- 任何作業系統（Windows / Linux / macOS）— 預設使用 SQLite，零外部 DB 依賴

### 設定憑證（**第一次跑必做**）

從 S24 起，**金鑰來源已經是 SQLite**，不再讀 `appsettings.*.json` 的 `BingX.ApiKey` / `BingX.ApiSecret`。首次啟動 Dashboard 會偵測到沒有 active `ExchangeAccount`、跳 **onboarding Modal** 要你先設定；或你可以直接去 **`/settings/exchanges`** 頁面新增一組 BingX 帳號（手動輸入 API Key + Secret，勾「設為當前啟用」送出），引擎會即時透過 `IExchangeCredentialProvider.CredentialsChanged` 事件重建 SDK client，不必重啟服務。

Discord 通知（可選）仍在 `appsettings.Local.json`：

```jsonc
{
  "Discord": {
    "Enabled":    true,
    "WebhookUrl": "https://discord.com/api/webhooks/.../..."
  }
}
```

> ⚠️ 強烈建議只存 **BingX Demo (VST)** 額度的金鑰。專案預設 `TradingMode: "Demo"`；切到 Live 前 Modal 會二次確認。DB 檔 (`cryptobot.db`) 已加入 `.gitignore`，金鑰不會被 commit，但本地仍是 plain text 儲存——不要把 DB 檔傳到別的機器。

### 啟動

```bash
git clone <this-repo>
cd CryptoBot

dotnet build           # 應該看到 0 警告 / 0 錯誤
dotnet test            # 全綠

dotnet run --project src/CryptoBot.ConsoleApp
```

啟動後終端機會顯示：

```
🌐 Web UI 指揮中心：http://localhost:5080
```

### 三個主要頁面

- **`http://localhost:5080/`** — 戰情儀表板（Dashboard）· 含策略手動控制台（大腦下拉 + Running Toggle）
- **`http://localhost:5080/settings/exchanges`** — 交易所金鑰管理（S24）
- **`http://localhost:5080/lab`** — Backtest Lab · 玻璃擬態優化室

第一次跑回測：在 `/lab` 頁面點頂部 `SMA Crossover` pill（已預設選中），調整 Fast/Slow 範圍與時間窗，按 **🚀 開始優化掃描**。Engine load 變金黃，進度條開始走，跑完出 Leaderboard，按某列「套用」即可 hot-swap 到正在跑的策略。

### CLI 模式（純回測，不啟 Web）

```bash
dotnet run --project src/CryptoBot.ConsoleApp -- backtest <args>
```

---

## 🗺️ Roadmap / Changelog · 開發歷程 (S1 → S25)

> 從零到 Beta v0.2 的全部里程碑。每個 SXX 對應一份 `HANDOFF_N.md` 交接文件。

### Phase 1 · 基礎設施與 API 串接 (S1–S6)

| Sprint | 內容 |
|---|---|
| **S1** | 專案骨架：Clean Architecture 四層 csproj + DI 雛形 + Solution 檔 |
| **S2** | Domain 核心：`Symbol` / `Price` / `Quantity` / `Money` / `Leverage` ValueObject + `Order` / `Position` / `Strategy` Aggregates |
| **S3** | Repository 介面定義（Domain）+ EF Core `AppDbContext` 雛形（Infrastructure） |
| **S4** | BingX SDK 探針：以 build-time XML 路徑解析 SDK 真實簽章（避免訓練資料誤導） |
| **S5** | `BingXExchangeClient` REST 包裝：下單 / 查倉 / 取 K 線 — 全程 decimal-strict |
| **S6** | `BingXMarketDataStream`：強型別 WS handlers + ListenKey 30-min 自動續期 + expired 事件政策（不自動重訂閱避免與 SDK auto-reconnect 競賽） |

### Phase 2 · 核心交易引擎與狀態同步 (S7–S12)

| Sprint | 內容 |
|---|---|
| **S7** | `IStrategy` 介面 + `SmaCrossoverStrategy` 第一版實作（純函式、Domain-only） |
| **S8** | `StrategyExecutor` + `StrategyRuntimeHostedService` 主迴圈；S7 整合測試 (test-drive) 落地 |
| **S9** | `AccountSynchronizer`：每 N 秒 reconcile balances/positions/orders 寫 EF；`PositionSizingService` Domain Service 完成 |
| **S10** | Order / Position 領域狀態機（Pending → Filled / Cancelled、Open → Closed / Liquidated）+ Domain 事件 |
| **S11** | EF Core repos + SQLite 持久化全鏈接通；migration / `AutoMigrateOnStartup` |
| **S12** | `InitialStrategySeeder` + Discord Webhook 通知（重大成交事件即時推播） |

### Phase 3 · 回測緩存與並行優化器 (S13–S16)

| Sprint | 內容 |
|---|---|
| **S13** | `IHistoricalDataProvider` + `IHistoricalKlineStore`（SQLite 緩存），下載一次後永遠離線 |
| **S14** | `BacktestSimulator` + `BacktestEngine`：滑點 / 手續費 / warmup bars，產出 `BacktestReport` |
| **S15** | `StrategyOptimizer`：`ParameterRange` 笛卡兒積展開、scope-per-run 並行、最後 P/DD 排名 |
| **S16** | CLI `BacktestRunner` 子命令；DataProvider 端串接 BingX REST 歷史補單 |

### Phase 4 · Blazor 戰情室與動態熱切換 (S17–S21)

| Sprint | 內容 |
|---|---|
| **S17** | `Microsoft.NET.Sdk.Web` 改造 ConsoleApp；Blazor Server + SignalR + Minimal API 同進程；`TradeHub` + `DashboardEventBus` + `SignalRRealtimeBroadcaster` 雙通道推播 |
| **S18** | Backtest Lab 第一版：`BacktestLab.razor` 整合 `OptimizationOrchestrator` + Leaderboard + Apply hot-swap；`#121212` 玻璃擬態 + 金黃進度條 + ETA + skeleton + fade-in |
| **S19** | **動態策略插槽**：`StrategyCatalog` 模組化目錄、`LabStateContainer` 狀態艙、`StrategyParameterFormBase` 抽象、`SmaParameterForm` 抽出、`StrategyTabsBar` pills nav |
| **S20** | **CryptoBot Dev Protocol** 開發憲章 v1.0 + `/docs/architecture/` 三份 Mermaid 文件（System / Data Flow / UML） |
| **S21** | **Demo↔Live 熱切換安全機制**：`IEnvironmentSwitcher` 編排（Stop-First → Reconfigure → Restart）+ `BingXExchangeClient` / `BingXMarketDataStream` SDK client `_clientGate` 原子換 + `GlobalStatusBar` MODE/ACTIVE/ENGINE/SWITCH 四段式狀態列 + `EnvironmentSwitchModal` Demo→Live 二次確認（autofocus 取消 + acknowledgment checkbox）+ 切換後策略**不**自動重啟（強制人為再確認）|

### Phase 5 · 指標庫、回測快取、熱切換與金鑰管理 (S22–S25)

| Sprint | 內容 |
|---|---|
| **S22** | **指標函式庫抽取**：SMA / RSI / Bollinger / ATR 純函式指標抽到 `Application.Indicators`；策略只呼叫 helper、不再自寫迴圈 |
| **S23** | **回測快取與 Kline 對齊修復**：`IHistoricalKlineStore` 按 `(Symbol, Interval, Range)` 鍵化；時段重疊 fold-in；warmup bars 嚴格對齊；同一組參數回測不再重算 |
| **S24** | **動態金鑰管理**：`ExchangeAccount` Aggregate + `IExchangeAccountRepository`（`SetActiveAsync` 交易化保證同交易所唯一 active）+ `/settings/exchanges` Blazor 頁面 + `IExchangeCredentialProvider` 抽象 + `DbExchangeCredentialProvider` (Singleton + `IServiceScopeFactory`) + `CredentialsChanged` 事件驅動 BingX SDK client / socket 原子重建 + Dashboard onboarding Modal |
| **S25** | **儀表板手動控制台**：Dashboard 內建「策略大腦」下拉（由 `IStrategyFactory.KnownTypes` 驅動）+ Running/Stopped Toggle · API `GET /api/strategies/available-types` + `PUT /api/strategies/{id}/type` · HostedService `ChangeStrategyTypeAsync` 以 `_mutateLock` 序列化 Stop→ChangeType→Rebuild Executor→Start · Domain `Strategy.ChangeType` 在 Running 時直接 throw 作為最後防線 |
| **🎉 Release** | **Beta v0.2** — 0 Warning / 0 Error · 全測試綠 · Dev Protocol v1.2 · `v0.2-beta` git tag |

### 🔮 Next（Phase 6 預告）

- 第二顆策略大腦上線：**RSI + Bollinger Bands** 短線反轉模型（綁到 S25 下拉選單即可熱切）
- WalkForward / OOS validation 進回測管線
- 多 Symbol 同時 live trading（目前 BTC-USDT only）
- Equity curve / Drawdown 曲線即時 chart
- 金鑰加密存儲（DPAPI / SQLCipher）取代目前的 plain-text

---

## 📁 專案結構

```
CryptoBot/
├─ src/
│  ├─ CryptoBot.Domain/            純核心：Aggregate / VO / 介面
│  ├─ CryptoBot.Application/       業務 use case：IStrategy / Backtest / Sync
│  ├─ CryptoBot.Infrastructure/    EF Core + BingX SDK + SignalR adapter
│  └─ CryptoBot.ConsoleApp/        Composition root：Blazor + Hub + APIs
│     ├─ Components/               Blazor 元件
│     │  ├─ Pages/                   Dashboard · BacktestLab · ExchangeSettings (/settings/exchanges)
│     │  ├─ Lab/                     SmaParameterForm · StrategyTabsBar
│     │  └─ Layout/                  MainLayout · GlobalStatusBar · EnvironmentSwitchModal
│     ├─ Lab/                      StrategyCatalog · LabStateContainer · StrategyParameterFormBase
│     ├─ Realtime/                 TradeHub · DashboardEventBus · OptimizationEvents
│     ├─ Services/                 OptimizationOrchestrator
│     └─ Api/                      Minimal API endpoints
├─ tests/
│  ├─ CryptoBot.Domain.Tests/
│  └─ CryptoBot.Application.Tests/
├─ docs/architecture/              Mermaid 架構圖
├─ memory/                         AI session memory（HANDOFF chain）
├─ CryptoBot_Dev_Protocol.md       👮 開發憲章
└─ README.md                       👋 你在這
```

---

## 📜 Licence & Disclaimer

純個人量化研究專案。**所有交易行為使用 BingX Demo 額度**，本專案不對任何真實資金損失負責。
若你 fork 後接上真實 API key，後果自負。記得先讀 `CryptoBot_Dev_Protocol.md` §3 的零容忍清單。

---

🤖 _Built with Claude (Opus 4.7) · Powered by .NET · 一行一行親手敲。_
