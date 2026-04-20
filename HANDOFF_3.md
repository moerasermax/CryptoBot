# CryptoBot 專案交接文件 #3（接續 HANDOFF_2.md）

> **給新對話 Claude**：請先讀 `HANDOFF.md` → `HANDOFF_2.md` → 本文件。本文件是第三輪對話的交接，主要內容：`BingXMarketDataStream.cs` 的進度與**尚未解決的 SDK 簽章問題**。

---

## 📌 環境現況（2026-04-19 更新）

- **.NET 8.0** / C# latest / Nullable reference types 啟用
- **JK.BingX.Net 3.10.0**（2026-04-09 釋出，CryptoExchange.Net 11.1.0）
- **Microsoft.Extensions.\* 10.0.1**
- 其他套件未變動

---

## ✅ 第三輪對話完成內容

### `BingXMarketDataStream.cs` — 骨架完成，但**尚無法編譯通過**

| 檔案 | 路徑 | 狀態 |
|------|------|------|
| `BingXMarketDataStream.cs` | `src/CryptoBot.Infrastructure/Exchange/BingX/` | ⚠️ 骨架完成，卡在 SDK lambda 型別 |

**已完成的部分（不要重寫）**：

- 類別完整結構：`StartAsync` / `StopAsync` / `SubscribeKlinesAsync` / `SubscribeMarkPriceAsync` / `UnsubscribeAsync` / `DisposeAsync` 都有實作
- `ConcurrentDictionary<SubscriptionKey, UpdateSubscription>` 管理多訂閱，避免重複訂閱
- 連線生命週期事件掛鉤：`ConnectionLost` / `ConnectionRestored` / `Exception`
- Payload 用 `dynamic` + `try/catch` 多候選名擷取欄位（和 REST 端 `GetOpenPositionsAsync` / `GetTradingRulesAsync` 同風格）
- 三處 SDK 呼叫 `ct: ct` 具名 ✅
- `BingXEnvironment.Demo/Live` 加 `global::` 前綴 ✅
- 預設 `UseDemoTrading = true`、0 個 double/float ✅
- 私有頻道訂閱失敗時退化成「REST 輪詢後援」，不拖垮啟動
- `BingX` 永續合約 WS 無獨立 MarkPrice topic → `SubscribeMarkPriceAsync` 改訂閱 Ticker 取 LastPrice 近似

---

## 🚨 卡住的關鍵問題：lambda 的 handler 參數型別

SDK v3.x 把 handler 從 `ExchangeEvent<T>` 改成 `Action<DataEvent<T>>`（v3.0.0 release notes 明確寫），
但我目前寫的 lambda：

```csharp
.SubscribeToKlineUpdatesAsync(
    symbol.BingXFormat,
    bxInterval,
    update => _ = HandleKlineUpdate(symbol, interval, update),  // ← update 型別?
    ct: ct)
```

`update` 的型別會被 C# 編譯器要求匹配 `DataEvent<SDK 具體型別>`，但我不確定：
- K 線的具體型別名（可能是 `BingXFuturesKline` / `BingXFuturesStreamKline` / 其他）
- Ticker 的具體型別名
- `SubscribeToUserDataUpdatesAsync` 的 handler 參數名（是 `onOrderUpdate/onAccountUpdate/onConfigUpdate`，還是別的組合）

---

## 🎯 下一輪工作：三件 F12 確認 + 修正編譯

### 步驟 1：F12 確認以下 SDK 簽章

在 Visual Studio 2022 裡打開 `BingXMarketDataStream.cs`，游標點到方法名按 F12 進到 SDK 定義，抄下完整簽章：

1. **`_socketClient.PerpetualFuturesApi.SubscribeToKlineUpdatesAsync`**
   - 完整參數列表（尤其 `Action<DataEvent<???>>` 的 `???` 是什麼型別）
   - 例：`Task<CallResult<UpdateSubscription>> SubscribeToKlineUpdatesAsync(string symbol, KlineInterval interval, Action<DataEvent<BingXFuturesKline>> onMessage, CancellationToken ct = default)`

2. **`_socketClient.PerpetualFuturesApi.SubscribeToTickerUpdatesAsync`**
   - 同上，特別是 handler 的泛型型別

3. **`_socketClient.PerpetualFuturesApi.SubscribeToUserDataUpdatesAsync`**
   - 所有 handler 參數名稱和順序（可能有 `onOrderUpdate` / `onAccountUpdate` / `onConfigUpdate` / `onListenKeyExpired` / `onBalanceUpdate` / `onPositionUpdate` 等不同組合）
   - 是否需要先 `StartUserStreamAsync` 拿 listenKey（Binance 家族需要，BingX 不一定）

### 步驟 2：修正 BingXMarketDataStream.cs 以過編譯

拿到 F12 結果後：
- 把 lambda 的 `update` 型別改成 `DataEvent<正確型別名>`
- 把 `SubscribeToUserDataUpdatesAsync` 的 handler 參數名調整成正確的
- 視情況加更多 `dynamic` 欄位讀取的候選名（目前 kline 的候選已覆蓋 OpenPrice/Open、OpenTime/Timestamp/OpenTimestamp/Time 等）

### 步驟 3：若有 v3.10.0 新 WS 方法再評估
- `SubscribeToIncrementalOrderBookUpdatesAsync`（v3.2.0 加入）— 目前不需要，之後做 order book analysis 再加
- 踩坑記錄：新增 v3.10.0 Websocket 類踩坑到 HANDOFF_4.md

---

## 📋 後續任務（依序，沿用 HANDOFF_2）

1. ⚠️ **修正 `BingXMarketDataStream.cs`** 讓它過編譯（需 F12 結果）
2. `Infrastructure/Persistence/` — AppDbContext + 4 個 Repository + UnitOfWork（EF Core 8 + SQLite）
3. `Infrastructure/Notifications/` — DiscordNotificationService + ConsoleNotificationService
4. `Infrastructure/DependencyInjection.cs` — `AddInfrastructure()` 擴充方法
5. `Application/DependencyInjection.cs` — `AddApplication()` 擴充方法
6. `CryptoBot.ConsoleApp/` — Program.cs、BotHostedService、appsettings.json
7. `Application/Backtesting/` — BacktestEngine、HistoricalDataLoader
8. 單元測試（Domain + Application）
9. 文件（SETUP.md / DEPLOYMENT.md / RISK_WARNING.md / ARCHITECTURE.md）

---

## ⚠️ 既有原則（重申）

1. 金融計算一律 `decimal`
2. 所有 async method 接 `CancellationToken`，SDK 呼叫必須具名 `ct: ct`
3. Nullable reference types 已啟用
4. Domain 零依賴不可破
5. Application 不依賴 Infrastructure
6. Aggregate 狀態變更只透過方法
7. Symbol 一律用 `symbol.BingXFormat`
8. 預設 `UseDemoTrading = true`

---

## 📚 本輪新增 SDK 知識（v3.10.0）

從 GitHub 主分支 README 確認的事實：

- **v3.0.0 (2025-12-16)** — Handler 型別從 `ExchangeEvent<T>` 改為 `Action<DataEvent<T>>`
- **v3.9.0 (2026-03-24)** — `ApiCredentials` → `BingXCredentials`（HANDOFF_2 踩坑 #2 已是正確寫法）
- **v3.10.0 (2026-04-09)** — 只新增 REST model 欄位（NeedsTagOrMemo、DisplayName 等），**WebSocket 簽章無變動**
- **v1.2.0 (2024-06-02)** — 新增 `SubscribeToKlineUpdatesAsync` / `SubscribeToTickerUpdatesAsync` / `SubscribeToPartialOrderBookUpdatesAsync`
- **v1.11.2 (2024-10-21)** — `SubscribeToUserDataUpdatesAsync order update model` 新增 `ReduceOnly` 屬性（證實此方法存在）
- **v2.8.0 (2025-09-30)** — 新增 `restClient.PerpetualFuturesApi.ExchangeData.GetTradingRulesAsync` 端點（**REST 端既有 `GetTradingRulesAsync` 用 dynamic 保底可以替換成強型別！** 待下一輪處理）
- **v2.2.2 (2025-07-17)** — PerpetualFutures WS 無資料檢查從 10s 改 40s（server ping 間隔從 5s 改 30s）

---

## 💡 給新對話 Claude 的提醒

- 本輪嚴格遵守了「不盲寫」原則，寧可留下未過編譯的骨架等 F12 確認
- 若使用者一次給齊 3 個 F12 結果，下一輪應能**直接**把 Stream 改成過編譯版本
- 不要再重寫 BingXExchangeClient.cs / BingXMapper.cs / BingXMarketDataStream.cs 的結構，只改細節
- REST 端的 `GetTradingRulesAsync`（用 dynamic）現在有原生端點可用（v2.8.0 引入），下一輪可替換成強型別（非急迫）

**準備好了就等使用者貼 F12 結果，然後收尾 BingXMarketDataStream.cs！**
