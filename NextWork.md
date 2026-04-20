# 策略執行引擎 — Phase 2 啟動

## 📖 前置狀態
基礎建設大圓滿：
- BingX REST/WS 強型別層完成（0 警告 0 錯誤）
- EF Core + SQLite 自動遷移跑通
- Console Host + appsettings.json（AfterBuild `<Copy>`）
- 連線心跳已實時抓到 BingX 現貨報價

勘察後確認：專案早已鋪好 **拉式 (Poll) 策略模型** 的骨架。本階段不重造輪子，聚焦「把現有輪子裝上車軸」。

---

## 📐 本專案採用的策略模型：**拉式 (Poll)**

> Executor 收到 Kline 事件 → 組歷史視窗 + 市場快照 + 當前持倉 → 同步呼叫 `IStrategy.AnalyzeAsync(...)` → 拿到 `TradingSignal` → 送風控 → 下單。
>
> 策略本身**無狀態、純函式**，所有狀態由 Executor / Repository 負責。

這個設計已經內建在：
- `src/CryptoBot.Application/Strategies/IStrategy.cs` — `AnalyzeAsync(config, klines, snapshot, positions, ct) → TradingSignal`
- 3 個具體策略：`TrendFollowingStrategy` / `MeanReversionStrategy` / `BasisArbitrageStrategy`
- Domain 值物件：`Kline`、`MarketSnapshot`、`TradingSignal`
- `IMarketDataStream`（Application）：事件驅動，`event Func<Symbol, KlineInterval, Kline, Task>? OnKlineUpdate`
- `BingXMarketDataStream : IMarketDataStream`（Infrastructure）已實作完整

---

## ✅ S1 & S2（本波衝刺）— 範疇已大幅縮小

### S1. 確認策略抽象層現狀（無需動程式碼）
- [x] `IStrategy`、`TradingSignal`、`Kline`、`MarketSnapshot` 皆已就位
- [x] Domain 零依賴已維持（只引用 System.*）
- 本次僅做 **勘察 & 認證** — 編譯不動

### S2. 補齊 `IMarketDataStream` 的 DI 註冊
- [ ] `BingXMarketDataStream` 註冊成 Singleton
- [ ] `IMarketDataStream` 以 factory 方式解析到同一個 `BingXMarketDataStream` 實例
- [ ] `BingXExchangeClient` 同時以具體型別 + `IExchangeClient` 兩條線註冊（ListenKey 記憶已載明此約束）
- [ ] 編譯 0 警告 0 錯誤

---

## ⚠️ 核心約束條件
- **🚨 10% 額度預警**：低於 10% 立即暫停並存檔。
- **Domain 零依賴**：維持現狀，介面一律放 Application。
- **decimal-strict**：延用現有 `Price` / `Quantity` 值物件。
- **Demo-only**：目前不觸發任何真實下單路徑。

---

## 🏁 完成標準
1. `dotnet build` 全綠，0 警告 0 錯誤。
2. DI 能同時解析 `IMarketDataStream` 與具體 `BingXMarketDataStream`，回傳同一個 Singleton 實例。
3. 回報 DI 修改 diff + build 結果。

---

## 🔭 下一波衝刺：S3 + S4（即將進入）
- **S3. StrategyExecutor**：訂閱 Kline → 組歷史視窗 → `AnalyzeAsync` → OrderIntent → RiskGate → IExchangeClient
- **S4. RiskGate**：單筆上限、最大持倉、日虧上限、冷卻時間

## 🔭 更後續
- **S5. 訂單／倉位同步器**：WS user-data → Repo；啟動時 reconciliation
- **S6. StrategyRuntimeHostedService**：取代純心跳的 HostedService
- **S7. 第一個具體策略（vertical slice）**
- **S8. 測試層補強**（Domain + Application）
- **S9. HANDOFF_6.md + 更新 memory/project_current_phase.md**
