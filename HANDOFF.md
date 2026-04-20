# CryptoBot 專案交接文件

> **給新對話 Claude**：這份文件說明專案目前進度與後續任務。請先完整閱讀本文件，再開始工作。

---

## 📌 專案概要

C# 量化交易機器人，使用 BingX 永續合約。採用 **Clean Architecture + DDD** 四層架構。

- **語言**：C# / .NET 8.0
- **IDE**：Visual Studio 2022 (Windows)
- **交易所**：BingX（使用 [JKorf.BingX.Net](https://github.com/JKorf/BingX.Net) v3.2.1）
- **交易標的**：永續合約（USDT-M Perpetual Futures）
- **策略類型**：趨勢跟蹤、均值回歸、現貨/合約基差套利
- **測試本金**：1000 TWD（約 33 USDT），後續升級至 10000 TWD
- **靈活切幣種**：透過 `Symbol` Value Object 實現

---

## 🏗️ 架構依賴規則（必須嚴格遵守）

```
ConsoleApp ───> Infrastructure ───> Application ───> Domain
                      │                   │
                      └───────────────────┘
                      (皆可依賴 Domain)
```

**嚴格禁止**：
- ❌ Domain 層不得引用任何其他專案或 NuGet 套件
- ❌ Application 層不得引用 Infrastructure
- ❌ Domain 不得引用 Application 或 Infrastructure

**允許**：
- ✅ Application 可用 MediatR、FluentValidation、Microsoft.Extensions.*
- ✅ Infrastructure 可用任何 NuGet 套件

---

## ✅ 已完成部分（勿重做）

### Domain 層（100% 完成）

| 分類 | 類別/檔案 |
|------|----------|
| **Common** | `Entity<T>`, `ValueObject`, `AggregateRoot<T>`, `IDomainEvent` |
| **Enums** | `OrderSide`, `OrderType`, `OrderStatus`, `PositionSide`, `PositionMode`, `MarginMode`, `KlineInterval`, `SignalType`, `StrategyStatus` |
| **Value Objects** | `Symbol` (**核心**，支援 BingX "BTC-USDT" 格式), `Price`, `Quantity`, `Leverage`, `Money` |
| **MarketData** | `Kline` (含 IsBullish/IsBearish/TypicalPrice 等), `MarketSnapshot` (含 Basis/BasisPercent 用於套利) |
| **Order Aggregate** | `Order`（完整狀態機 + 工廠方法 CreateLimitOrder/CreateMarketOrder/CreateStopMarketOrder）|
| **Position Aggregate** | `Position`（含止損、止盈、追蹤停損、加減倉、強平價估算）|
| **Strategy Aggregate** | `Strategy` (狀態機 + 績效統計), `StrategyConfiguration`, `TradingSignal` |
| **Events** | 15+ 種領域事件 |
| **Services** | `PositionSizingService`（基於風險%的倉位計算）|
| **Repositories 介面** | `IOrderRepository`, `IPositionRepository`, `IStrategyRepository`, `IUnitOfWork` |
| **Exceptions** | `DomainException`, `RiskManagementException`, `InsufficientMarginException` |

### Application 層（85% 完成）

| 分類 | 類別/檔案 |
|------|----------|
| **Interfaces** | `IExchangeClient` (含 `SymbolTradingRules`/`ExchangePositionInfo` records), `IMarketDataStream`, `INotificationService` |
| **Indicators** | `TechnicalIndicators` 靜態類：SMA, EMA, RSI, Bollinger Bands, MACD, ATR |
| **Strategies** | `IStrategy` 介面 + 三個實作：`TrendFollowingStrategy`, `MeanReversionStrategy`, `BasisArbitrageStrategy` |
| **RiskManagement** | `RiskManager` (多層檢查) + `RiskLimits` (Conservative/Moderate/Aggressive) |
| **Trading** | `StrategyExecutor`（訊號→下單協調器）|

### Infrastructure 層（15% 完成）

- 專案檔（已引用：JKorf.BingX.Net 3.2.1、EF Core 8.0.11、Serilog 4.2.0、Discord.Net.Webhook 3.17.0）
- `Configuration/BotOptions.cs`（含 `BingXOptions`、`BotOptions`、`DiscordOptions`）

---

## 🚧 待完成任務（依優先順序）

### 1️⃣ Infrastructure/Exchange/BingX（最關鍵）

**檔案位置**：`src/CryptoBot.Infrastructure/Exchange/BingX/`

- **`BingXExchangeClient.cs`** — 實作 `IExchangeClient` 
  - 使用 `BingXRestClient.PerpetualFuturesApi`
  - Symbol 轉換：`symbol.BingXFormat`（格式 "BTC-USDT"）
  - 映射 BingX 回傳值到 Domain 物件
  - 處理 API 速率限制
  
- **`BingXMarketDataStream.cs`** — 實作 `IMarketDataStream`
  - 使用 `BingXSocketClient.PerpetualFuturesApi`
  - WebSocket 斷線重連機制
  - 並發訂閱多個 Symbol
  
- **`BingXMapper.cs`** — 型別映射輔助類
  - `KlineInterval` → BingX 的字串代碼
  - `OrderType`/`OrderSide`/`PositionSide` 相互轉換

**BingX API 注意事項**：
- Symbol 格式是 **"BTC-USDT"** 帶連字號
- 永續合約的 API 路徑在 `/openApi/swap/v2/...`
- API Key 需要在 BingX 網頁「API 管理」建立，開啟「合約交易」和「現貨交易」權限
- 建議 IP 白名單綁定
- 有模擬盤環境（demo trading），先在 demo 測試 2-4 週

### 2️⃣ Infrastructure/Persistence

- **`AppDbContext.cs`**（EF Core 8 + SQLite）
- **`Repositories/OrderRepository.cs`、`PositionRepository.cs`、`StrategyRepository.cs`、`UnitOfWork.cs`**
- **Value Object 映射**：用 owned types 或 value converters
  - `Symbol` → 存為 "BTC-USDT" 字串
  - `Price`/`Quantity` → decimal(38,18)
  - `Leverage` → int
- 初始化 migration

### 3️⃣ Infrastructure/Notifications

- **`DiscordNotificationService.cs`** — 實作 `INotificationService`（用 Discord.Net.Webhook）
- **`ConsoleNotificationService.cs`** — 備用（僅 log）
- 根據 `DiscordOptions.Enabled` 決定注入哪個

### 4️⃣ Infrastructure/DependencyInjection

- **`DependencyInjection.cs`** — 提供 `IServiceCollection.AddInfrastructure(IConfiguration)` 擴充方法
- Application 層也要有 `AddApplication()` 擴充方法

### 5️⃣ ConsoleApp（進入點）

**檔案位置**：`src/CryptoBot.ConsoleApp/`

- **`CryptoBot.ConsoleApp.csproj`**
- **`Program.cs`** — 組合根，用 `Host.CreateDefaultBuilder`
- **`appsettings.json`** — 配置範本（API Key 留空讓使用者填）
- **`appsettings.Development.json`** — 開發環境覆寫
- **`Services/BotHostedService.cs`** — `IHostedService`，執行策略輪詢迴圈
  - 每 `TickIntervalSeconds` 秒拉一次 K 線 + snapshot
  - 對每個啟用的策略執行 `StrategyExecutor.ExecuteTickAsync`
  - 優雅關閉處理

**appsettings.json 範例結構**：
```json
{
  "BingX": {
    "ApiKey": "",
    "ApiSecret": "",
    "UseDemoTrading": true
  },
  "Bot": {
    "ActiveSymbols": ["BTC-USDT", "ETH-USDT"],
    "KlineInterval": "FifteenMinutes",
    "Leverage": 3,
    "RiskPerTradePercent": 0.02,
    "StopLossPercent": 0.02,
    "TakeProfitPercent": 0.04,
    "EnabledStrategies": ["TrendFollowing"],
    "TickIntervalSeconds": 60
  },
  "Discord": {
    "Enabled": false,
    "WebhookUrl": ""
  },
  "Serilog": { ... }
}
```

### 6️⃣ 回測框架

- **`CryptoBot.Application/Backtesting/BacktestEngine.cs`**
  - 餵歷史 K 線一根一根回放
  - 模擬訂單成交（考慮滑點）
  - 計算績效：夏普比率、最大回撤、勝率、盈虧比、卡瑪比率
- **`HistoricalDataLoader.cs`** — 從 BingX 拉歷史 K 線並快取到本機 SQLite

### 7️⃣ 測試

**`tests/CryptoBot.Domain.Tests/`**（xUnit + FluentAssertions）
- `OrderTests.cs`（狀態機轉換、部分成交、拒絕）
- `PositionTests.cs`（開倉、止損觸發、止盈觸發、追蹤停損、強平價計算）
- `SymbolTests.cs`（Parse 各種格式）
- `PositionSizingServiceTests.cs`

**`tests/CryptoBot.Application.Tests/`**
- `TrendFollowingStrategyTests.cs`（mock 歷史數據測訊號邏輯）
- `RiskManagerTests.cs`（mock Repository/Exchange）

### 8️⃣ 文件

- **`README.md`**（根目錄）— 專案說明 + Quick Start
- **`docs/SETUP.md`** — 環境設定、BingX API Key 申請步驟
- **`docs/DEPLOYMENT.md`** — VPS 部署（建議 systemd + screen/tmux，進階可 Docker）
- **`docs/RISK_WARNING.md`** — **必要**：合約交易風險警告（爆倉可能性、黑天鵝事件、建議先 demo 盤 2-4 週）
- **`docs/ARCHITECTURE.md`** — 架構圖、領域模型說明

---

## ⚠️ 重要約束

1. **金融計算一律用 `decimal`**，絕不用 `double`/`float`
2. **所有非同步方法加 `CancellationToken`** 參數
3. **Nullable reference types 已啟用**
4. **Value Objects 不可變，自我驗證**（建構子或工廠方法裡 throw DomainException）
5. **Aggregate 狀態變更只能透過方法**，不得直接改 property（所有 setter 是 private）
6. **BingX Symbol 格式是 "BTC-USDT"**，使用 `Symbol.BingXFormat` 取得
7. **預設槓桿不超過 5x**，Leverage VO 的 max 是 20
8. **預設啟用 demo trading**（`UseDemoTrading = true`），正式上線才改 false

---

## 🔑 關鍵設計細節

### Symbol 靈活切換幣種

```csharp
// 從字串解析（兩種格式都支援）
var sym1 = Symbol.Parse("BTC-USDT");
var sym2 = Symbol.Parse("BTCUSDT");
// 取得 BingX 格式
string bingxSym = symbol.BingXFormat;  // "BTC-USDT"
```

### Position 止損止盈工作流程

```csharp
// 每次收到新價格時
var triggerSignal = position.UpdateCurrentPrice(newPrice);
if (triggerSignal != SignalType.None)
{
    // StrategyExecutor 會處理平倉
}
```

### 策略訊號 → 下單流程（在 StrategyExecutor）

1. `IStrategy.AnalyzeAsync()` 產生 `TradingSignal`
2. `PositionSizingService` 算出數量
3. `IRiskManager.CheckBeforeOpenAsync()` 多層檢查
4. `IExchangeClient.SetLeverageAsync()` 設定槓桿
5. 建立 `Order.CreateMarketOrder(...)` 並呼叫 `IExchangeClient.PlaceOrderAsync(order)`
6. 等待成交，用 `Position.Open(...)` 建立持倉

---

## 📂 專案結構（最終目標）

```
CryptoBot/
├── CryptoBot.sln
├── README.md
├── HANDOFF.md（本檔）
├── src/
│   ├── CryptoBot.Domain/               ✅ 完成
│   ├── CryptoBot.Application/          ✅ 85%
│   │   └── Backtesting/                ⬜ 待建
│   ├── CryptoBot.Infrastructure/       ⬜ 15%
│   │   ├── Exchange/BingX/             ⬜ 待建（最關鍵）
│   │   ├── Persistence/                ⬜ 待建
│   │   ├── Notifications/              ⬜ 待建
│   │   └── DependencyInjection.cs      ⬜ 待建
│   └── CryptoBot.ConsoleApp/           ⬜ 待建
├── tests/
│   ├── CryptoBot.Domain.Tests/         ⬜ 待建
│   └── CryptoBot.Application.Tests/    ⬜ 待建
└── docs/
    ├── SETUP.md                        ⬜ 待建
    ├── DEPLOYMENT.md                   ⬜ 待建
    ├── RISK_WARNING.md                 ⬜ 待建
    └── ARCHITECTURE.md                 ⬜ 待建
```

---

## 💡 建議的工作順序

1. **先做 `BingXExchangeClient`** — 這是最關鍵的。做完就能驗證 API 串通。
2. 再做 `BingXMarketDataStream` — 提供即時行情
3. 做 EF Core `AppDbContext` + Repository 實作
4. 做 `Notifications`
5. 做 `DependencyInjection.cs`
6. 做 `ConsoleApp` 讓整個專案能跑起來
7. 做回測框架（可獨立於上面完成）
8. 補單元測試
9. 寫文件

---

## 🚨 安全提醒（請在 RISK_WARNING.md 中強調）

告知使用者：
- 合約交易爆倉是常態，不是意外
- **先用 demo trading 跑 2-4 週**再上實盤
- 1000 TWD 本金扣除手續費後，策略優勢可能不明顯
- 槓桿超過 10x 極度危險
- 極端行情（如 LUNA 崩盤、2022/11 FTX 事件）可能整單爆倉
- 沒有穩賺不賠的策略

---

**準備好了就從 `BingXExchangeClient.cs` 開始吧！如果有任何已完成檔案想確認細節，可以問使用者要內容。**
