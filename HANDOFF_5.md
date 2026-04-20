# CryptoBot 專案交接文件 #5（接續 HANDOFF_4.md）

> **給新對話 Claude**：請先讀 `HANDOFF.md` → `HANDOFF_2.md` → `HANDOFF_3.md` → `HANDOFF_4.md` → 本文件。
> 本文件記錄第五輪對話：**Persistence 層完成**（EF Core 8 + SQLite，0 編譯錯誤）+ ListenKey 記憶永久化。

---

## 📌 環境現況（2026-04-20 更新）

- **.NET 8.0** / C# latest / Nullable 啟用
- **JK.BingX.Net 3.10.0** + **EF Core 8.0.11** + **EFCore.Sqlite 8.0.11**
- **Infrastructure 專案 `dotnet build`：0 errors，9 warnings**
  - 2 既有 BingX warnings（HANDOFF_4 line 80 / 388）
  - 7 新增：Price? 屬性掛 non-nullable PriceConverter 的 nullability variance（CS8620），運作不受影響

---

## ✅ 第五輪對話完成內容

### 1. ListenKey 記憶永久化（NextWork.md 第一階段）

claude-mem MCP 後端的 search 端點仍 500（list_corpora 雖通但 corpora=0），無法直寫 Chroma Observation。改寫入跨 session 的檔案記憶：

- 新增 `~/.claude/projects/.../memory/reference_bingx_listenkey.md`
- 收錄：REST 三方法簽章、強型別 WS handler、30 分鐘續期循環、`onConfigurationUpdate` 修正、ListenKeyExpired 處理策略
- `MEMORY.md` 索引從 4 → 5 條

### 2. Persistence 層（NextWork.md 第二階段）

| 檔案 | 路徑 | 狀態 |
|------|------|------|
| `AppDbContext.cs` | `Persistence/` | ✅ |
| `UnitOfWork.cs` | `Persistence/` | ✅（含 Begin/Commit/Rollback） |
| `SymbolConverter.cs` | `Persistence/ValueConverters/` | ✅ Symbol → "BTC-USDT" |
| `PriceConverter.cs` | 同上 | ✅ |
| `QuantityConverter.cs` | 同上 | ✅ |
| `LeverageConverter.cs` | 同上 | ✅ |
| `StrategyConfigurationConverter.cs` | 同上 | ✅ JSON column |
| `OrderConfiguration.cs` | `Persistence/Configurations/` | ✅ |
| `PositionConfiguration.cs` | 同上 | ✅ |
| `StrategyConfiguration.cs`（class 名 `StrategyEntityConfiguration`） | 同上 | ✅ 避開與 Domain VO 撞名 |
| `OrderRepository.cs` | `Persistence/Repositories/` | ✅ |
| `PositionRepository.cs` | 同上 | ✅ |
| `StrategyRepository.cs` | 同上 | ✅ |

**設計決策**：
- Symbol VO ValueConverter 將其映射為單一字串（NextWork 明確要求），`Symbol.Parse` 已能還原
- StrategyConfiguration 走 JSON column（含 Symbol/Leverage/Parameters dict），用內部 record DTO 保持序列化穩定
- 所有 Repository 內部 `Add/Update` 皆同步操作 DbSet，`SaveChangesAsync` 由 UnitOfWork 一次提交（典型 DDD/UoW 模式）
- Symbol 查詢過濾改用 `EF.Property<string>(o, nameof(Order.Symbol)) == symbol.BingXFormat`，繞開 `ValueObject.==` 無法翻譯為 SQL 的限制

### 3. 重大發現（避免後續走冤枉路）

**Repository 介面實際在 `CryptoBot.Domain.Repositories.IRepositories.cs`，不在 Application 層。**

我一開始在 `Application/Common/Interfaces/` 重建了 `IOrderRepository` / `IPositionRepository` / `IStrategyRepository` / `IUnitOfWork`，編譯時撞到 ambiguous reference 才發現 Domain 層早已定義且被 Application 既有程式碼（`StrategyExecutor`、`RiskManager`）重度依賴。

**已刪除誤建的 4 個 Application 介面檔，Repository 實作對齊 Domain 既有合約**（`AddAsync`/`UpdateAsync` async 簽章、`GetOpenPositionsBySymbolAsync` 命名、`includeClosedPositions` 參數名等）。

> 雖然把 Repository 介面放 Domain 不是 100% 純 Clean Architecture（傳統建議放在 Application），但既有 codebase 已成形，本輪不重構此處。後續若要回歸標準，需同步調 Domain → Application 並修改 `StrategyExecutor`/`RiskManager` 的 using。

---

## 🚧 後續任務（依優先順序）

### 優先 P1（接續 NextWork 路線）
1. ~~`Infrastructure/Persistence/`~~ ✅ **本輪完成**
2. **`Infrastructure/Notifications/`** — `DiscordNotificationService`、`ConsoleNotificationService`（介面 `INotificationService` 已存在於 Application）
3. **`Infrastructure/DependencyInjection.cs` — `AddInfrastructure()`**
   - `BingXExchangeClient` 必須註冊為 `IExchangeClient` AND 自身具體型別
   - `AppDbContext` 走 `UseSqlite(connStr)`，從 `appsettings.json` 讀
   - 三個 Repository + UnitOfWork 皆 `Scoped`
   - 第一次 run 需執行 `EnsureCreatedAsync()` 或加 migration（建議走 migration）
4. **`Application/DependencyInjection.cs` — `AddApplication()`**
5. **`CryptoBot.ConsoleApp/`** — `Program.cs` / `BotHostedService` / `appsettings.json`
6. **首次 EF Core Migration**：`dotnet ef migrations add InitialCreate -p src/CryptoBot.Infrastructure -s src/CryptoBot.ConsoleApp`

### 優先 P2
7. `Application/Backtesting/`
8. 單元測試（Domain + Application）
9. 文件更新

### 小優化（任何時候）
- 7 個 CS8620 nullable variance warnings（Price? 屬性 + non-nullable PriceConverter）。最乾淨的修法是另寫 `NullablePriceConverter : ValueConverter<Price?, decimal?>` 或改 lambda 形式 `HasConversion(v => v == null ? null : v.Value, v => v == null ? null : Price.Create(v.Value))`
- HANDOFF_4 提到的 BingXExchangeClient line 80 / 388 兩個 CS8629 仍未處理

---

## 📚 EF Core 8 + Domain VO 經驗（值得記住）

1. **Symbol 查詢用 EF.Property**：`ValueObject.==` 是靜態運算子，EF 翻譯不出，必須改用 `EF.Property<string>(entity, columnName) == convertedValue` 走底層欄位比對
2. **Nullable VO 屬性**：`Price?` 配 `ValueConverter<Price, decimal>`（non-nullable）會出 CS8620，但 EF 實作有自動處理 null（NULL column 不過 converter）。要清警告就寫 `ValueConverter<Price?, decimal?>`
3. **複合 VO 走 JSON**：`StrategyConfiguration` 含字典與其他 VO，最簡解是內部 record DTO + `System.Text.Json` 序列化，在 ValueConverter 內完成。`HasColumnType("TEXT")` 強制 SQLite 用文字欄位
4. **Aggregate 計算屬性**：`Position.UnrealizedPnL` / `Order.IsActive` 等 computed 屬性必須在 EntityTypeConfiguration `b.Ignore(...)` 排除，否則 EF 試圖建欄位會炸
5. **`DomainEvents` 集合**：所有 Aggregate Root 都有 `IReadOnlyCollection<IDomainEvent> DomainEvents`，必須 `b.Ignore(...)` 排除
6. **Strategy 配置類別命名衝突**：`Domain.Aggregates.StrategyAggregate.StrategyConfiguration`（VO）與 EntityTypeConfiguration<Strategy> 撞名 — 我把後者命名為 `StrategyEntityConfiguration` 並用 `using DomainStrategy = ...` alias 規避

---

## ⚠️ 既有原則（重申）

1. 金融計算一律 `decimal`（本輪所有 Repository / Converter 都遵守）
2. 所有 async method 接 `CancellationToken`
3. **Domain 零依賴**：本輪所有 EF Core 引用都在 Infrastructure，Domain 層未動 ✅
4. Aggregate 狀態變更只透過方法
5. 預設 `UseDemoTrading = true`

---

## 💡 給新對話 Claude 的提醒

- **下一步預設動 P1#2 (Notifications)**，再 P1#3 (DI wiring)
- DI 寫好後第一次跑前必須建立 SQLite DB schema：`dotnet ef migrations add InitialCreate` → `dotnet ef database update`，或在 ConsoleApp 啟動時呼叫 `await ctx.Database.MigrateAsync()`
- `INotificationService` 介面定義在 `CryptoBot.Application.Common.Interfaces` — 先打開來確認簽章再實作
- claude-mem Chroma 後端在 session 5 初仍部份失能（`search` 500，`list_corpora` 通），檔案記憶系統是真正持久層，新增了 `reference_bingx_listenkey.md`
