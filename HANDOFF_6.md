# CryptoBot 專案交接文件 #6（接續 HANDOFF_5.md）

> **給新對話 Claude**：請按序閱讀 `HANDOFF.md` → `HANDOFF_2.md` → `HANDOFF_3.md` → `HANDOFF_4.md` → `HANDOFF_5.md` → 本文件。
> 本文件記錄 Phase 2 收尾（S7 全線試車 + S8 應用層整合 + S9 文檔 + S10 通知系統）。Phase 2 **完工**：0 編譯錯誤、0 警告、46 / 46 測試全綠。

---

## 📌 環境現況（2026-04-21）

- .NET 8.0 / C# latest / Nullable 啟用
- JK.BingX.Net 3.10.0、EF Core 8.0.11 + Sqlite、Discord.Net.Webhook 3.17.0（目前未使用，自己打 webhook）
- `dotnet build CryptoBot.sln`：**0 error / 0 warning**
- `dotnet test CryptoBot.sln`：**1 Domain + 45 Application = 46 通過 / 0 失敗**

---

## ✅ Phase 2 完工總結

### 1. 策略執行引擎（Application 層，完整管線）

```
IMarketDataStream.OnKlineUpdate (BingX WS)
   │
   ├─► StrategyExecutor.HandleKlineUpdateAsync  [per strategy, per symbol×interval]
   │      │  (SemaphoreSlim 0-wait — 上一根還沒跑完就跳過這根)
   │      ▼
   │   ProcessKlineAsync
   │      │  1. 更新滾動 K 線視窗 (LinkedList<Kline>, cfg.MaxKlineWindow)
   │      │  2. GetMarketSnapshotAsync
   │      │  3. CreateAsyncScope → 取 scoped: IPositionRepository / IRiskManager / IOrderSizer / IOrderRepository / IUnitOfWork
   │      │  4. positionRepo.GetByStrategyIdAsync(..., includeClosedPositions: false)
   │      │  5. IStrategy.AnalyzeAsync → TradingSignal
   │      │
   │      ▼  (signal.Type != None)
   │   HandleSignalAsync
   │      │  1. IOrderSizer.ComputeAsync → Quantity
   │      │  2. IRiskManager.CheckBeforeOpenAsync → approval + reason
   │      │  3. Order.CreateMarketOrder (domain aggregate)
   │      │  4. IExchangeClient.PlaceOrderAsync (成功後會 AssignExchangeOrderId)
   │      │  5. IOrderRepository.AddAsync + IUnitOfWork.SaveChangesAsync
   │      │  6. IStrategyCooldownTracker.RecordOrderPlaced
   │      │  7. Log 🚀 [STRATEGY-MATCH] + INotificationService.NotifyTradeAsync
   │      │     (通知失敗不炸管線 — try/catch 只 log Warning)
   │
   └─► AccountSynchronizer (掛 OnExchangeOrderUpdate / OnExchangeAccountUpdate handlers)
          │
          ├─► OnExchangeOrderUpdate: GetByExchangeOrderIdAsync → ApplyUpdate → UpdateAsync → SaveChanges
          └─► OnExchangeAccountUpdate: 對 DB 現有 Position 做 qty/price 同步；qty=0 的 → Close
```

**關鍵協作者（Application 層）**：

| 元件 | 生命週期 | 職責 |
|------|---------|------|
| `IStrategyCooldownTracker` | Singleton | 跨策略共用冷卻表（策略 ID → 上次下單時間） |
| `IRiskManager` | Scoped | 開倉前檢查（餘額足、未超併發、未破風險上限） |
| `IOrderSizer` | Scoped | 由策略設定 + 餘額 + 市價計算開倉量 |
| `IStrategy`（具體實作）| Singleton | 純指標演算法，無狀態；目前有 TrendFollowing / MeanReversion / BasisArbitrage / **SmaCrossover** |
| `IStrategyFactory` | Singleton | 依 `StrategyType` 字串從 DI 取具體 `IStrategy` |
| `IStrategyExecutorFactory` | Singleton | 為每個 `Strategy` 造一個 `StrategyExecutor`（持有滾動視窗與 tick 鎖） |
| `IAccountSynchronizer` | Singleton | 掛 WS handler、做 startup reconcile |
| `StrategyRuntimeHostedService` | HostedService | 啟動順序：Market.Start → Sync.Start → Sync.Reconcile → Load Running strategies → Executor.Start。停機反向 |
| `INotificationService` | Singleton | 預設 `NoOpNotificationService`；有設定時 Infrastructure 以 `Replace` 覆蓋為 `DiscordNotificationService` |

### 2. 資料庫 Seed 規則（Infrastructure/Seeding/InitialStrategySeeder）

**觸發點**：`Program.Main` 在 migration 後、`RunAsync` 前呼叫 `seeder.SeedAsync()`。

**邏輯**（冪等）：
1. 讀 `StrategySeedOptions`（appsettings.json 的 `"StrategySeed"` section）。若 `Enabled=false` → log + return。
2. 開 DI scope，取 `IStrategyRepository`。
3. `GetByNameAsync(cfg.Name)` — 若已存在同名策略 → skip。
4. 用 `StrategyConfiguration.Create` 建立 VO（Symbol / Interval / Leverage / Risk%/SL%/TP% / MaxKlineWindow / FastSmaPeriod / SlowSmaPeriod），再 `Strategy.Create(name, type, config)`。
5. `cfg.StartImmediately=true` → `strategy.Start()`（狀態 → `Running`）。
6. `repo.AddAsync` + `uow.SaveChangesAsync`。

**appsettings.json 對應區塊**（當前配置）：
```json
"StrategySeed": {
  "Enabled": true,
  "Name": "SMA-BTC15m-TestDrive",
  "StrategyType": "SmaCrossover",
  "Symbol": "BTC-USDT",
  "KlineInterval": "FifteenMinutes",
  "Leverage": 3,
  "RiskPerTradePercent": 0.02,
  "StopLossPercent": 0.02,
  "TakeProfitPercent": 0.04,
  "MaxKlineWindow": 200,
  "FastSmaPeriod": 20,
  "SlowSmaPeriod": 50,
  "StartImmediately": true
}
```

要加新策略：目前 Seeder 只種「一筆」。多策略種子改造方向 — 把 `StrategySeedOptions` 改成 `Strategies: StrategySeedEntry[]`，迴圈 seeding 即可。

### 3. SMA 交叉策略（Application/Strategies/SmaCrossover）

- `StrategyType` 字串：`"SmaCrossover"`。
- Parameters：`FastSmaPeriod`（預設 20）、`SlowSmaPeriod`（預設 50），從 `StrategyConfiguration.Parameters` dict 讀取。
- 訊號規則：
  - `Golden cross`（fast 由下往上穿 slow）→ 無持倉時 `OpenLong`；持有空倉時 `CloseShort`；持有多倉時 `None`。
  - `Death cross`（fast 由上往下穿 slow）→ 無持倉時 `OpenShort`；持有多倉時 `CloseLong`；持有空倉時 `None`。
  - `klines.Count < slowPeriod + 1` 或價格完全平坦 → `None`。
- 指標計算：共用 `TechnicalIndicators.SMA` helper，避免在策略檔內重覆實作。

### 4. 通知系統（S10）

**Application 層**（`CryptoBot.Application.Common.Interfaces.INotificationService`，原本就有定義 + `NotificationLevel` enum）：
```csharp
Task NotifyAsync(string title, string message, NotificationLevel level, CancellationToken ct);
Task NotifyTradeAsync(string symbol, string action, decimal price, decimal quantity, CancellationToken ct);
Task NotifyErrorAsync(Exception ex, CancellationToken ct);
```
- `NoOpNotificationService`（Application/Notifications/）— 無配置時的後備實作。
- `AddApplication()` 用 `TryAddSingleton` 註冊 NoOp，所以 Application 單獨使用（或跑單元測試）時就有可用實例。

**Infrastructure 層**（`CryptoBot.Infrastructure.Notifications.DiscordNotificationService`）：
- HttpClient-based：單一 Singleton HttpClient 對固定 webhook URL POST JSON（不啟用 `IHttpClientFactory`，因為 webhook 呼叫頻率極低）。
- Embed 格式：`title + description + color`，顏色由 `NotificationLevel` 切換（Info 藍 / Warn 黃 / Error 橘 / Critical 紅）。`Critical` 會帶 `@everyone`。
- `NotifyTradeAsync` 固定綠色，帶 action + price + quantity 三欄。
- **任何發送錯誤吞掉 + Warning log** — 通知層絕對不能把交易管線炸掉。

**DI 決策邏輯**（`AddInfrastructure → AddNotifications`）：
```csharp
if (discordCfg.Enabled && !string.IsNullOrWhiteSpace(discordCfg.WebhookUrl))
    services.Replace(ServiceDescriptor.Singleton<INotificationService>(sp => new DiscordNotificationService(...)));
// 否則 Application 層 TryAddSingleton 的 NoOp 保留
```

**觸發點**（`StrategyExecutor.HandleSignalAsync` 最後）：
```csharp
_logger.LogInformation("🚀 [STRATEGY-MATCH] ...");  // Serilog（檔案 + console）
try { await _notifications.NotifyTradeAsync(symbol, action, price, qty, ct); }
catch (Exception ex) { _logger.LogWarning(ex, "Notification dispatch failed ..."); }
```

**啟用流程**（給使用者）：
1. Discord server → 頻道設定 → Integrations → Webhooks → New Webhook → Copy Webhook URL。
2. 修改 `src/CryptoBot.ConsoleApp/appsettings.json`：
   ```json
   "Discord": {
     "Enabled": true,
     "WebhookUrl": "https://discord.com/api/webhooks/xxxxx/yyyyy"
   }
   ```
3. 重啟 ConsoleApp；下一次 `[STRATEGY-MATCH]` 觸發會同時收到 log 與 Discord 訊息。
4. 沒設 URL 或 `Enabled=false` 時完全 zero-cost（NoOp 直接回傳 `Task.CompletedTask`）。

### 5. 測試涵蓋（46 項）

**CryptoBot.Domain.Tests（1）**：
- `KlineTests` — `Kline.Create` 的邊界條件（high/low/open/close 關係）。

**CryptoBot.Application.Tests（45）**，分布如下：

| 目錄 | 數量 | 涵蓋內容 |
|------|------|---------|
| `Strategies/StrategyEngineTests.cs` | 3 | StrategyExecutor 下單路徑、None 訊號不下單、冷卻擋第二單 |
| `Strategies/SmaCrossover/SmaCrossoverStrategyTests.cs` | 6 | Golden / Death cross、持倉反轉訊號、K 線不足、平盤不觸發 |
| `Strategies/StrategyFactoryTests.cs` | 3 | Type 字串對應、不存在時的例外、大小寫不敏感 |
| `RiskManagement/OrderSizerTests.cs` | 5 | 風險量換算、RiskPercent 邊界、槓桿、餘額不足、零量 |
| `RiskManagement/RiskManagerTests.cs` | 6 | 併發上限、總風險上限、餘額不足、同 symbol 擠佔 |
| `RiskManagement/StrategyCooldownTrackerTests.cs` | 4 | 新策略無冷卻、記錄後進冷卻、時間過後解除、多策略獨立 |
| `Synchronization/AccountSynchronizerTests.cs` | 8 | OrderUpdate / AccountUpdate 雙路徑、Reconcile 對帳、找不到 order 時略過、Position qty=0 觸發 Close |
| `Strategies/StrategyRuntimeHostedServiceTests.cs` | 6 | Start/Stop 順序、載入 Running 策略、reconcile 失敗不炸、空策略庫 idle |
| `Integration/S7TestDriveIntegrationTests.cs` | 3 | **全線 E2E**：一根 K 線 → Analyze → Sizer → Risk → PlaceOrder → Repo；OrderUpdate → Synchronizer 同步 Filled；Seed 策略啟動 |
| `Strategies/StrategyEngineTests.cs` 其它 | 1 | Start/Stop dispose 正常 |

整合測試 `S7TestDriveIntegrationTests` 以 `ServiceCollection` 組一整組 in-memory fake（OrderRepo / PositionRepo / StrategyRepo / UnitOfWork / MarketDataStream / ExchangeClient），註冊真正的 Application 層服務（`RiskManager` / `OrderSizer` / `SmaCrossoverStrategy` / `StrategyFactory` / `StrategyExecutorFactory` / `AccountSynchronizer` / `NoOpNotificationService`），手動 fire 一根黃金交叉 K 線走完整管線。

---

## 🧱 遺留與下一階段（Phase 3 候選）

1. **Backtesting** — 給歷史 K 線，用 `IStrategy` 重放產生虛擬 Order/Position 時間序列；`Application/Backtesting/` 目錄目前空。
2. **多策略 seeding** — 把 `StrategySeedOptions` 改為陣列，支援同時開多個策略。
3. **策略熱插拔** — `StrategyRuntimeHostedService._executors` 目前是 `List<T>`（啟停單執行緒假設）；要熱加策略需換 `ConcurrentBag` 或另上 lock。
4. **通知系統擴張** — Telegram / Email 實作可照 Discord 模式（Infrastructure 層 Replace）；若需多 channel 同時送，把單一 Singleton 改為 composite pattern。
5. **Infrastructure/Exchange 健壯性** — `BingXMarketDataStream` 在 listenKey expired 時僅清 state（HANDOFF_4 的設計），實務要補 HostedService 層級的 Stop→Start 復原。
6. **Trading 目錄殘留** — `Application/Trading/StrategyExecutor.cs` 是舊版孤兒（無任何引用，但仍會編譯），下次整理時可確認是否要刪。

---

## 🗂 關鍵檔案索引（新增於 Phase 2 收尾）

```
src/CryptoBot.Application/
  Notifications/NoOpNotificationService.cs         [新增] INotificationService 預設實作
  Strategies/SmaCrossover/SmaCrossoverStrategy.cs  [新增] SMA 20/50 交叉
  Strategies/StrategyExecutor.cs                   [修改] 注入 INotificationService，HandleSignal 結尾 NotifyTradeAsync
  Strategies/StrategyExecutorFactory.cs            [修改] 工廠多塞一個 INotificationService
  DependencyInjection.cs                           [修改] TryAddSingleton NoOp、註冊 SmaCrossoverStrategy

src/CryptoBot.Infrastructure/
  Notifications/DiscordNotificationService.cs      [新增] HttpClient webhook POST
  Seeding/InitialStrategySeeder.cs                 [新增] 冪等策略種子
  Configuration/StrategySeedOptions.cs             [新增] Options 綁定
  DependencyInjection.cs                           [修改] AddNotifications + seeder 註冊

src/CryptoBot.ConsoleApp/
  Program.cs                                       [修改] SeedInitialStrategyAsync 呼叫
  appsettings.json                                 [修改] StrategySeed + Discord section

tests/CryptoBot.Application.Tests/
  Strategies/SmaCrossover/SmaCrossoverStrategyTests.cs  [新增] 6 純指標邏輯測試
  Integration/S7TestDriveIntegrationTests.cs            [新增] 3 全線 E2E
  Strategies/StrategyEngineTests.cs                     [修改] ctor 多塞 NoOp 通知
```

---

Phase 2 完工。接下 Phase 3 的人可以直接從「遺留與下一階段」清單挑項目進入。
