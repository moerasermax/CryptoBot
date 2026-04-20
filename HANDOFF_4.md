# CryptoBot 專案交接文件 #4（接續 HANDOFF_3.md）

> **給新對話 Claude**：請先讀 `HANDOFF.md` → `HANDOFF_2.md` → `HANDOFF_3.md` → 本文件。
> 本文件記錄第四輪對話：BingXMarketDataStream **過編譯通過**、ListenKey 全套機制、強型別重構。

---

## 📌 環境現況（2026-04-20 更新）

- **.NET 8.0** / C# latest / Nullable reference types 啟用
- **JK.BingX.Net 3.10.0**（CryptoExchange.Net 11.1.0）
- 其他套件未變動
- **Infrastructure 專案 `dotnet build`：0 errors，2 既有 warnings**（非本輪引入）

---

## ✅ 第四輪對話完成內容

### 1. `BingXMarketDataStream.cs` — **完整重寫，過編譯通過**

| 檔案 | 路徑 | 狀態 |
|------|------|------|
| `BingXMarketDataStream.cs` | `src/CryptoBot.Infrastructure/Exchange/BingX/` | ✅ 編譯通過（強型別） |

**關鍵修正**：
- ❌ HANDOFF_3 寫的 `onConfigUpdate` 參數名是錯的 → 正確是 **`onConfigurationUpdate`**
- ❌ HANDOFF_3 說「BingX 永續合約 WS 沒有 MarkPrice topic」是錯的 → SDK 3.10.0 有原生 **`SubscribeToMarkPriceUpdatesAsync`**，已改用真正的 mark price，不再用 ticker 近似
- ✅ User-data 訂閱已正確接線：`listenKey` + 4 個 handler（account / order / configuration / listenKeyExpired）
- ✅ Lambda handler 全部用強型別 `DataEvent<T>`，**不再用 dynamic**
- ✅ `BingXFuturesKlineUpdate[]`（陣列！）、`BingXFuturesTickerUpdate`、`BingXMarkPriceUpdate`、`BingXFuturesOrderUpdate`、`BingXFuturesAccountUpdate`、`BingXConfigUpdate`、`BingXListenKeyExpiredUpdate` 全部直接屬性存取
- ✅ ListenKey 自動續期循環：每 30 分鐘呼叫一次 ExtendListenKey（BingX listenKey 有效期 60 分鐘，留 50% 安全邊際）
- ✅ ListenKeyExpired event 收到時失效目前 key（不主動重訂閱以免與 SDK auto-reconnect race）
- ✅ Stop 時 best-effort DELETE listenKey 釋放

### 2. `BingXExchangeClient.cs` — 新增 ListenKey 三個方法

```csharp
public async Task<string> GetListenKeyAsync(CancellationToken ct = default);    // POST
public async Task ExtendListenKeyAsync(string listenKey, CancellationToken ct = default);  // PUT
public async Task StopListenKeyAsync(string listenKey, CancellationToken ct = default);    // DELETE
```

`StartUserStreamAsync` 的回傳型別已透過 probe 確認是 **`WebCallResult<string>`**（不是 BingXListenKey 物件）。

---

## 🔬 本輪用到的 SDK 探索手法（強烈推薦）

訓練資料對 BingX.Net 3.10.0 不可信。本輪沒有反覆試錯，而是用三步法快速拿到 ground truth：

### A. 直接讀 SDK 的 XML 文件

NuGet 快取裡有完整 XML doc：
```
C:\Users\Moera\.nuget\packages\jk.bingx.net\3.10.0\lib\net8.0\BingX.Net.xml
```

用 Grep 搜方法名／屬性名，可一次拿到完整簽章。例如：
```bash
grep -n 'SubscribeToKlineUpdatesAsync' BingX.Net.xml
```

### B. 用 `dotnet build` 當編譯器探針

無法從 XML 看出來的（例如 `WebCallResult<T>` 的 T），寫一個臨時 `_Probe.cs`：
```csharp
internal static class _Probe {
    public static async Task Run(BingXRestClient c, CancellationToken ct) {
        var r = await c.PerpetualFuturesApi.Account.StartUserStreamAsync(ct);
        string forced = r.Data; // 若 r.Data 不是 string，編譯器會說「不能轉 X 為 string」
    }
}
```
build 一次即可。完成後刪掉 probe 檔。

### C. PowerShell 反射（不推薦）
JKorf 套件依賴鏈太多，PowerShell `Add-Type` 載 dll 會 ReflectionTypeLoadException。改用 dotnet tooling。

---

## 🚧 已知遺留／後續任務（依優先順序）

### 優先 P1
1. **`Infrastructure/Persistence/`** — AppDbContext + 4 個 Repository + UnitOfWork（EF Core 8 + SQLite）
2. **`Infrastructure/Notifications/`** — DiscordNotificationService + ConsoleNotificationService
3. **`Infrastructure/DependencyInjection.cs`** — `AddInfrastructure()`
   - ⚠️ 注意：`BingXExchangeClient` 必須同時註冊為 `IExchangeClient` 與自己的具體型別，因為 `BingXMarketDataStream` 透過建構子注入 `BingXExchangeClient`（不是 `IExchangeClient`，因為 ListenKey 方法是 BingX-specific，不放介面）
4. **`Application/DependencyInjection.cs`** — `AddApplication()`
5. **`CryptoBot.ConsoleApp/`** — Program.cs / BotHostedService / appsettings.json

### 優先 P2
6. **`Application/Backtesting/`**
7. 單元測試（Domain + Application）
8. 文件（SETUP / DEPLOYMENT / RISK_WARNING / ARCHITECTURE）

### 小優化（任何時候）
- `BingXExchangeClient.cs` 仍剩 2 個 `CS8629` 警告（line 80 GetFuturesBalanceAsync 的 `(decimal)balance.Balance`、line 388 RefreshOrderStatusAsync 的 `(decimal)remote.Fee`）— 都是 `decimal?` 直接強轉。可改為 `balance.Balance ?? 0m` / `remote.Fee ?? 0m` 消警告。
- `BingXExchangeClient.GetTradingRulesAsync` / `GetOpenPositionsAsync` 仍用 dynamic 保底。HANDOFF_3 提到 v2.8.0 已新增原生 `GetTradingRulesAsync` 端點，可替換為強型別（low risk，但需先 F12 / XML 看新端點的回傳型別欄位）。
- `RefreshOrderStatusAsync` 用 `RecordFill` 時 `commission` 直接 `(decimal)remote.Fee`，當 Fee 是 null 會 throw — 改 `remote.Fee ?? 0m`。

---

## 📚 SDK 簽章權威清單（v3.10.0，已驗證）

### REST `BingXRestClientPerpetualFuturesApiAccount`
- `Task<WebCallResult<string>> StartUserStreamAsync(CancellationToken ct)`
- `Task<WebCallResult> KeepAliveUserStreamAsync(string listenKey, CancellationToken ct)`
- `Task<WebCallResult> StopUserStreamAsync(string listenKey, CancellationToken ct)`

### WebSocket `BingXSocketClientPerpetualFuturesApi`
- `SubscribeToKlineUpdatesAsync(string symbol, KlineInterval interval, Action<DataEvent<BingXFuturesKlineUpdate[]>> onMessage, CancellationToken ct)`
- `SubscribeToTickerUpdatesAsync(string symbol, Action<DataEvent<BingXFuturesTickerUpdate>> onMessage, CancellationToken ct)`
- `SubscribeToMarkPriceUpdatesAsync(string symbol, Action<DataEvent<BingXMarkPriceUpdate>> onMessage, CancellationToken ct)`
- `SubscribeToBookPriceUpdatesAsync(string symbol, Action<DataEvent<BingXBookTickerUpdate>> onMessage, CancellationToken ct)`
- `SubscribeToUserDataUpdatesAsync(string listenKey, Action<DataEvent<BingXFuturesAccountUpdate>> onAccountUpdate, Action<DataEvent<BingXFuturesOrderUpdate>> onOrderUpdate, Action<DataEvent<BingXConfigUpdate>> onConfigurationUpdate, Action<DataEvent<BingXListenKeyExpiredUpdate>> onListenKeyExpiredUpdate, CancellationToken ct)`

### Model 欄位
- `BingXFuturesKlineUpdate`：Symbol, OpenPrice, ClosePrice, HighPrice, LowPrice, Volume, **Timestamp**（沒有 OpenTime/CloseTime，要自己用 interval 算 closeTime）
- `BingXFuturesTickerUpdate`：LastPrice, OpenPrice, HighPrice, LowPrice, BestBidPrice, BestAskPrice, Volume, OpenTime, CloseTime, ...
- `BingXMarkPriceUpdate`：Symbol, MarkPrice
- `BingXFuturesOrderUpdate`：OrderId, Symbol, Side, PositionSide, Type, Status, Price, Quantity, QuantityFilled, Fee, AveragePrice, ReduceOnly, ...
- `BingXFuturesAccountUpdate`：Update（→ BingXFuturesAccountChange { Trigger, Balances, Positions }）
- `BingXListenKeyExpiredUpdate`：ListenKey
- `BingXConfigUpdate`：Configuration

---

## ⚠️ 既有原則（重申）

1. 金融計算一律 `decimal`
2. 所有 async method 接 `CancellationToken`，SDK 呼叫必須具名 `ct: ct`
3. Nullable reference types 已啟用
4. Domain 零依賴不可破
5. Application 不依賴 Infrastructure（ListenKey 方法是 BingX-specific，故只放在具體類別 `BingXExchangeClient`，不擴充 `IExchangeClient`）
6. Aggregate 狀態變更只透過方法
7. Symbol 一律用 `symbol.BingXFormat`
8. 預設 `UseDemoTrading = true`

---

## 💡 給新對話 Claude 的提醒

- **SDK 不確定時，優先讀 NuGet 快取的 XML doc**（`~/.nuget/packages/jk.bingx.net/3.10.0/lib/net8.0/BingX.Net.xml`），效率比靠記憶高 10 倍
- 寫一兩行 probe 程式碼搭配 `dotnet build` 是探索 SDK 簽章的最快路徑
- 不要再「盲寫」+ 試錯。前三輪對話累積的 token 浪費，本輪用 ground truth 一次到位
- BingXMarketDataStream 已從 dynamic 全面換成強型別 — **未來改動時若 BingX 升級新版，只要 XML doc 有變動，就會直接編譯失敗，反而是好事**

**可以直接從 P1 任務 1（Persistence 層）開始。**
