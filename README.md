# CryptoBot · BingX .NET 10 Algorithmic Trading Engine

> **Beta v0.1** — 一台跑在 .NET 上、專為 BingX 永續合約打造的量化交易引擎，
> 內建即時策略執行、SignalR 戰情室、SMA 並行優化回測、與模組化策略大腦插槽。
>
> _「不是 demo，是會自己賺錢（或燒錢）的引擎。」_

---

## ✨ Beta v0.1 亮點

| 模組 | 內容 |
|---|---|
| 🔁 **自動續期 ListenKey** | BingX user-data WS 強型別處理；30 分自動 PUT 續期；expired 事件交給上層 Stop→Start 復原 |
| 📊 **SMA 並行優化回測** | 笛卡兒積展開 Fast×Slow 網格，scope-per-run 並行回測，依 P/DD ratio 排名 |
| 📡 **SignalR 即時推播** | `DashboardEventBus` 雙通道：Blazor in-process + 外部 WebSocket 一致觸發 |
| 🧠 **動態策略插槽 UI** | `StrategyCatalog` + `DynamicComponent` — 加新策略只需 3 步，不必碰主頁 |
| 🪟 **玻璃擬態戰情室** | `#121212` 深色基底、Pills nav、金黃進度條、ETA、skeleton + fade-in |
| 🔒 **狀態艙 Singleton** | `LabStateContainer` 跨頁面/刷新/tab 不丟資料 |
| 🧪 **零容忍品質** | 0 Warning / 0 Error · 46/46 tests green |

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

### 啟動

```bash
git clone <this-repo>
cd CryptoBot

dotnet build           # 應該看到 0 警告 / 0 錯誤
dotnet test            # 應該看到 46/46 通過

dotnet run --project src/CryptoBot.ConsoleApp
```

啟動後終端機會顯示：

```
🌐 Web UI 指揮中心：http://localhost:5080
```

### 進入回測實驗室

開瀏覽器到：

- **`http://localhost:5080/`** — 戰情儀表板（Dashboard）
- **`http://localhost:5080/lab`** — Backtest Lab · 玻璃擬態優化室

第一次跑回測：在 `/lab` 頁面點頂部 `SMA Crossover` pill（已預設選中），調整 Fast/Slow 範圍與時間窗，按 **🚀 開始優化掃描**。Engine load 變金黃，進度條開始走，跑完出 Leaderboard，按某列「套用」即可 hot-swap 到正在跑的策略。

### CLI 模式（純回測，不啟 Web）

```bash
dotnet run --project src/CryptoBot.ConsoleApp -- backtest <args>
```

---

## 🗺️ Roadmap / Changelog · 開發歷程 (S1 → S20)

> 從零到 Beta v0.1 的全部里程碑。每個 SXX 對應一份 `HANDOFF_N.md` 交接文件。

### Phase 1 · 核心架構與 API 串接

| Sprint | 內容 |
|---|---|
| **S1** | 專案骨架：Clean Architecture 四層 csproj + DI 雛形 + Solution 檔 |
| **S2** | Domain 核心：`Symbol` / `Price` / `Quantity` / `Money` / `Leverage` ValueObject + `Order` / `Position` / `Strategy` Aggregates |
| **S3** | Repository 介面定義（Domain）+ EF Core `AppDbContext` 雛形（Infrastructure） |
| **S4** | BingX SDK 探針：以 build-time XML 路徑解析 SDK 真實簽章（避免訓練資料誤導） |
| **S5** | `BingXExchangeClient` REST 包裝：下單 / 查倉 / 取 K 線 — 全程 decimal-strict |

### Phase 2 · 交易引擎與風控

| Sprint | 內容 |
|---|---|
| **S6** | `IStrategy` 介面 + `SmaCrossoverStrategy` 第一版實作（純函式、Domain-only） |
| **S7** | `StrategyExecutor` + `StrategyRuntimeHostedService` 主迴圈；S7 整合測試 (test-drive) 落地 |
| **S8** | `AccountSynchronizer`：每 N 秒 reconcile 帳戶 → 寫 EF；`PositionSizingService` Domain Service 完成 |
| **S9** | `BingXMarketDataStream`：強型別 WS handlers + ListenKey 30-min 自動續期 + expired 事件政策 |
| **S10** | `InitialStrategySeeder` + Discord Webhook 通知（重大成交事件即時推播） |

### Phase 3 · 回測緩存與優化器

| Sprint | 內容 |
|---|---|
| **S11** | `IHistoricalDataProvider` + `IHistoricalKlineStore`（SQLite 緩存），下載一次後永遠離線 |
| **S12** | `BacktestSimulator` + `BacktestEngine`：滑點 / 手續費 / warmup bars，產出 `BacktestReport` |
| **S13** | `StrategyOptimizer`：`ParameterRange` 笛卡兒積展開、scope-per-run 並行、最後 P/DD 排名 |
| **S14** | CLI `BacktestRunner` 子命令；DataProvider 端串接 BingX REST 歷史補單 |

### Phase 4 · Blazor 戰情室 UI 與模組化封裝

| Sprint | 內容 |
|---|---|
| **S15** | `Microsoft.NET.Sdk.Web` 改造 ConsoleApp；Blazor Server + SignalR + Minimal API 同進程 |
| **S16** | `TradeHub` + `DashboardEventBus` + `SignalRRealtimeBroadcaster`：雙通道推播架構落地 |
| **S17** | Backtest Lab 第一版：`BacktestLab.razor` 整合 `OptimizationOrchestrator` + Leaderboard + Apply hot-swap |
| **S17.5** | 修 EF Core `Symbol` value converter 在 `EF.Property<string>` 路徑炸的問題；新增 `OrderRepo.GetRecentAsync` 繞開 |
| **S18** | UI/UX 大升級：`StrategyCatalog` 模組化目錄、`LabStateContainer` 狀態艙、`StrategyParameterFormBase` 抽象、`SmaParameterForm` 抽出、`StrategyTabsBar` pills nav、`#121212` 玻璃擬態 + 金黃進度條 + ETA + skeleton + fade-in |
| **S19** | **CryptoBot Dev Protocol** 開發憲章 + `/docs/architecture/` 三份 Mermaid 文件（System / Data Flow / UML） |
| **S20** | 🎉 **Beta v0.1 release** — README 重寫 + git tag `v0.1-beta` |

### 🔮 Next（Phase 5 預告）

- 第二顆策略大腦：**RSI + Bollinger Bands** — 短線反轉模型
- WalkForward / OOS validation 進回測管線
- 多 Symbol 同時 live trading（目前 BTC-USDT only）
- Equity curve / Drawdown 曲線即時 chart

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
│     │  ├─ Pages/                   /  /lab
│     │  ├─ Lab/                     SmaParameterForm · StrategyTabsBar
│     │  └─ Layout/                  MainLayout
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
