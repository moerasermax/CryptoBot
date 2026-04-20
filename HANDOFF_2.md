# CryptoBot 專案交接文件 #2（接續 HANDOFF.md）

> **給新對話 Claude**：這份文件是第二輪對話的交接紀錄。請**先讀 `HANDOFF.md`（第一份原始交接）**，再讀這份補充。本文件內容包含：上一輪完成的項目、JK.BingX.Net 3.10.0 的踩坑紀錄、以及下一步任務。

---

## 📌 環境現況（2026-04 更新）

- **.NET 8.0** / C# latest / Nullable reference types 啟用
- **JK.BingX.Net 3.10.0**（⚠️ 注意：原始 HANDOFF.md 寫 3.2.1，實際本機已升級到 3.10.0，兩版有大量 Breaking Changes）
- **Microsoft.Extensions.\* 10.0.1**（已升級以相容 BingX SDK）
- EF Core 8.0.11、Serilog 4.2.0、Discord.Net.Webhook 3.17.0（未變動）

---

## ✅ 第二輪對話完成內容

Infrastructure 層的 BingX 整合首批檔案已過編譯並可用：

| 檔案 | 路徑 | 狀態 |
|------|------|------|
| `BingXMapper.cs` | `src/CryptoBot.Infrastructure/Exchange/BingX/` | ✅ 編譯通過 |
| `BingXExchangeClient.cs` | `src/CryptoBot.Infrastructure/Exchange/BingX/` | ✅ 編譯通過 |

**這兩個檔案的實作已經對齊 BingX SDK v3.10.0，不要重寫**。如需調整，請用最小改動。

---

## ⚠️ BingX SDK v3.10.0 踩坑紀錄（**必讀**，避免重蹈覆轍）

訓練資料中的 BingX.Net 資訊多半基於舊版（2.x 或 3.2.x），以下是第二輪對話中**實際驗證過**的 v3.10.0 事實，請完全信任並使用：

### 1. 命名空間衝突（嚴重）
專案自己的 namespace `CryptoBot.Infrastructure.Exchange.BingX` 會跟 SDK 的 `BingX.Net.*` 互相遮蔽。

**解法**：所有 SDK 型別一律用 `global::` 前綴：
```csharp
global::BingX.Net.Enums.PositionSide.Long
global::BingX.Net.BingXEnvironment.Demo
global::CryptoExchange.Net.Objects.WebCallResult<T>
```

### 2. Credentials 建立方式（最終可用寫法）
- ❌ `new ApiCredentials(...)` → abstract，不可 new
- ❌ `new BingXApiCredentials(...)` → internal，外部看不到
- ❌ 建構子內 `opts.ApiCredentials = ...` → 泛型約束 `TApiCredentials` 無法滿足
- ✅ **正確寫法**：
```csharp
BingXCredentials creds = new BingXCredentials()
{
    Key = _options.ApiKey,
    Secret = _options.ApiSecret
};
_client.PerpetualFuturesApi.SetApiCredentials(creds);
_client.SpotApi.SetApiCredentials(creds);
```
注意是 `BingXCredentials` 不是 `BingXApiCredentials`，且用物件初始化器（property set），不用建構子。

### 3. Environment 切換（Demo vs Live）
```csharp
opts.Environment = _options.UseDemoTrading
    ? global::BingX.Net.BingXEnvironment.Demo
    : global::BingX.Net.BingXEnvironment.Live;
```

### 4. API 方法改名對照表
| 舊名（v3.2 / 猜測） | v3.10.0 實際名稱 |
|--------------------|-----------------|
| `GetMarkPriceAsync` | **不存在** → 改用 `GetBookTickerAsync`，取 (bid+ask)/2 當近似 |
| `GetPremiumIndexAsync` | **不存在** |
| `GetPricesAsync` | **不存在** |
| `SetMarginTypeAsync` | `SetMarginModeAsync` |
| `GetTickerAsync`（現貨） | `GetTickersAsync`（複數，回陣列） |

### 5. CancellationToken 陷阱（最常見錯誤）
SDK 許多 method 的第一個參數是 `string? symbol = null` 而非 `CancellationToken`。

**所有 `ct` 一律用具名參數 `ct: ct`**，否則 C# 會把 `CancellationToken` 塞給 `string? symbol`，產生「無法從 `CancellationToken` 轉換成 `string?`」的錯誤：

```csharp
// ❌ 錯：把 ct 當作 symbol
GetContractsAsync(ct)

// ✅ 對
GetContractsAsync(ct: ct)
```

**建議所有 SDK 方法呼叫的 ct 都具名**，即使目前沒錯也要加，防未來 overload 改變。

### 6. Nullable 回傳型別陷阱
v3.10.0 很多欄位改成 `decimal?`，但並非全部：

| 欄位 | 型別 |
|------|------|
| `BingXContract.MinOrderQuantity` | `decimal?` |
| `BingXContract.MinNotional` | `decimal?` |
| `BingXFuturesOrderDetails.AveragePrice` | `decimal?` |
| `BingXFuturesBookTicker.BestBidPrice` | **`decimal`（非 null）** |
| `BingXFuturesBookTicker.BestAskPrice` | **`decimal`（非 null）** |

對非 nullable 欄位加 `?? 0m` 會報「運算子 `??` 不可套用至 decimal 和 decimal」。遇到時先移除 `??`，編譯通過就對了。

### 7. Enum 改名對照
| 舊名 | v3.10.0 正確名 |
|------|---------------|
| `FuturesOrderType.Stop` | `StopLimit` |
| `FuturesOrderType.TakeProfit` | `TakeProfitLimit` |
| `KlineInterval.FourHour` | `FourHours`（複數） |
| `KlineInterval.SixHour` | `SixHours` |
| `KlineInterval.EightHour` | `EightHours` |
| `KlineInterval.TwelveHour` | `TwelveHours` |
| `KlineInterval.ThreeDays` | **`ThreeDay`**（**單數**，唯一例外） |

### 8. `BingXContract` 欄位名不確定（用 dynamic 保底）
`QuantityStep` / `PriceStep` / `MinNotional` 在 v3.10.0 都不存在（編譯錯）。實際欄位名可能是 `StepSize` / `TickSize` / `QuantityPrecision` / `PricePrecision` / `MinOrderValue`。

**目前 `GetTradingRulesAsync` 用 `dynamic` + try/catch 多候選名保底**。下一輪有機會時 F12 進 `BingXContract` 看實際屬性清單，再改強型別。

### 9. `BingXPosition` 欄位名不確定（同樣 dynamic 保底）
`PositionAmount` / `EntryPrice` / `PositionQuantity` / `AverageEntryPrice` 都可能不對。

**目前 `GetOpenPositionsAsync` 全用 `dynamic` + try/catch 保底**。同樣待 F12 確認後改強型別。

### 10. `WebCallResult.Check` 擴充方法需兩個版本
有泛型 `WebCallResult<T>` 和非泛型 `WebCallResult`。某些 `Set*` method 回非泛型，兩個版本的 `Check` 擴充都要保留。當前實作已包含兩版本，勿刪。

---

## 🚧 待完成任務（依優先順序）

### 🎯 下一步：`BingXMarketDataStream.cs`
**路徑**：`src/CryptoBot.Infrastructure/Exchange/BingX/`
**介面**：`CryptoBot.Application.Common.Interfaces.IMarketDataStream`

需求：
- 使用 `BingXSocketClient.PerpetualFuturesApi`
- WebSocket 斷線重連機制（參考 `BingXOptions.WebSocketReconnectDelayMs`）
- 並發訂閱多個 Symbol 的 K 線、標記價
- 訂單/持倉更新透過私有 WebSocket
- 遵守既有的 `BingXMapper` 型別映射
- **預期會遇到跟 REST 類似的版本差異**，請主動用 F12 確認方法/屬性簽名，不要盲寫

### 📋 後續（依序）
1. `Infrastructure/Persistence/` — AppDbContext + 4 個 Repository + UnitOfWork（EF Core 8 + SQLite）
2. `Infrastructure/Notifications/` — DiscordNotificationService + ConsoleNotificationService
3. `Infrastructure/DependencyInjection.cs` — `AddInfrastructure()` 擴充方法
4. `Application/DependencyInjection.cs` — `AddApplication()` 擴充方法
5. `CryptoBot.ConsoleApp/` — Program.cs、BotHostedService、appsettings.json
6. `Application/Backtesting/` — BacktestEngine、HistoricalDataLoader
7. 單元測試（Domain + Application）
8. 文件（SETUP.md / DEPLOYMENT.md / RISK_WARNING.md / ARCHITECTURE.md）

---

## ⚠️ 既有原則（重申）

1. **金融計算一律 `decimal`**，絕不用 `double`/`float`
2. **所有 async method 接 `CancellationToken`**，SDK 呼叫時**必須具名 `ct: ct`**
3. **Nullable reference types 已啟用**
4. **Domain 零依賴**不可破（不引用 SDK、不引用 Application 或 Infrastructure）
5. **Application 不依賴 Infrastructure**
6. **Aggregate 狀態變更只透過方法**，不碰 private setter
7. **Symbol 一律用 `symbol.BingXFormat`**（即 "BTC-USDT"）
8. **預設 `UseDemoTrading = true`**，正式上線才改 false

---

## 🔍 開始工作前的建議步驟

1. 解壓附上的 zip
2. 讀 `HANDOFF.md`（原始交接）
3. 讀本文件的「踩坑紀錄」章節（最重要）
4. 開啟 VS 2022，載入 `CryptoBot.sln`
5. 先 Build 確認現有專案能編譯通過
6. 讀現有的 `BingXExchangeClient.cs` 和 `BingXMapper.cs`，了解模式：
   - 擴充方法 `result.Check(nameof(XXX))` 的錯誤處理風格
   - `global::` 前綴的使用時機
   - `dynamic` 保底寫法的適用場景
7. 開始實作 `BingXMarketDataStream.cs`

---

## 💡 給新對話 Claude 的特別提醒

- 你的訓練資料裡的 BingX.Net 版本**很可能是舊版**，不要信任訓練記憶中的 API 名稱
- 遇到任何 SDK 型別/方法不確定時，**請求使用者 F12 確認**，不要盲寫
- 本輪對話耗費了大量 token 在反覆試錯。你有現成的「踩坑紀錄」，請充分利用
- 金融 bot 安全第一：對任何可能造成實際下單的邏輯，請多加 assertion 和 log

**準備好了就從 `BingXMarketDataStream.cs` 開始吧！**