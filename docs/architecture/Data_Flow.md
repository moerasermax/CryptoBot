# Data Flow · 從 BingX 到下單與 UI 的全鏈路

> 目標：一張圖看完「市場資料 → 策略訊號 → 風控 → 下單 → 持久化 → 即時推播 UI」整條管線。

## 1. Live Trading 主流程

```mermaid
flowchart LR
    %% ─── 外部 ───
    subgraph EXT["☁️ BingX (External)"]
        Rest["REST API<br/>/openApi/...<br/>(orders, klines, positions)"]
        WS["WebSocket<br/>市場 + User Data Stream<br/>(via ListenKey)"]
    end

    %% ─── Infrastructure 邊界 ───
    subgraph INF["🔌 Infrastructure"]
        Stream["BingXMarketDataStream<br/>· ListenKey 30-min renewal<br/>· strong-typed handlers"]
        Client["BingXExchangeClient<br/>· REST wrapper<br/>· decimal-strict"]
        Cache[("SQLite<br/>HistoricalKlineStore<br/>(EF Core)")]
        EFRepo["EF Core Repos<br/>OrderRepo / StrategyRepo / PositionRepo"]
        Broadcaster["SignalRRealtimeBroadcaster"]
    end

    %% ─── Application 業務 ───
    subgraph AP["⚙️ Application"]
        Sync["AccountSynchronizer<br/>· 每 N 秒 reconcile balances/positions/orders"]
        Exec["StrategyExecutor<br/>· 餵新 K 線給 IStrategy<br/>· 取得 TradingSignal"]
        Strategy["IStrategy<br/>(SmaCrossoverStrategy)"]
        Risk["PositionSizingService<br/>(Domain Service)"]
        Runtime["StrategyRuntimeHostedService<br/>(BackgroundService)"]
    end

    %% ─── ConsoleApp / UI ───
    subgraph UI["🖥️ ConsoleApp / UI"]
        Bus["DashboardEventBus"]
        Hub["TradeHub (SignalR)"]
        Blazor["Blazor Server<br/>Dashboard / Lab"]
        Discord["DiscordNotifier"]
    end

    %% ─── 流向 ───
    WS  ==>|tick / order / account| Stream
    Rest ==>|kline pull / order place| Client

    Stream ==>|decoded events| Sync
    Stream ==>|new kline| Exec
    Client -.upserts.-> Cache
    Client -.upserts.-> EFRepo

    Exec --> Strategy
    Strategy -->|TradingSignal| Risk
    Risk -->|sized order| Client

    Sync -->|state diff| EFRepo
    Sync -.raises.-> Bus
    Exec -.raises.-> Bus
    Client -.fill events.-> Bus

    Bus -->|c# event| Blazor
    Bus -->|fan-out| Broadcaster
    Broadcaster --> Hub
    Hub -.SignalR.-> Blazor
    Bus -->|critical fill| Discord

    Runtime -. orchestrates .-> Stream
    Runtime -. orchestrates .-> Exec
    Runtime -. orchestrates .-> Sync

    classDef ext fill:#0d3b66,stroke:#4dd0e1,color:#fff
    classDef inf fill:#1c1e26,stroke:#f5b301,color:#e6e8ee
    classDef ap fill:#1c1e26,stroke:#4dd0e1,color:#e6e8ee
    classDef ui fill:#1c1e26,stroke:#1de982,color:#e6e8ee
    class EXT,Rest,WS ext
    class INF,Stream,Client,Cache,EFRepo,Broadcaster inf
    class AP,Sync,Exec,Strategy,Risk,Runtime ap
    class UI,Bus,Hub,Blazor,Discord ui
```

## 2. Backtest / Optimization 流程（Lab）

```mermaid
sequenceDiagram
    autonumber
    participant U as 使用者 (Blazor /lab)
    participant FB as SmaParameterForm
    participant ST as LabStateContainer
    participant API as Minimal API<br/>POST /api/lab/optimize
    participant ORC as OptimizationOrchestrator
    participant HDP as IHistoricalDataProvider
    participant HKS as IHistoricalKlineStore
    participant OPT as StrategyOptimizer
    participant ENG as BacktestEngine
    participant BUS as DashboardEventBus
    participant HUB as TradeHub (SignalR)

    U->>FB: 調整 Fast/Slow Min/Max/Step
    FB->>ST: ParameterChanged → CurrentGridSize
    U->>API: 點「開始優化掃描」
    API->>ORC: TryStart(OptimizationRequest)
    ORC-->>API: 202 Accepted (gate locked)
    Note over ORC: Task.Run 背景執行

    ORC->>HDP: DownloadAsync(BTC-USDT, 1h, range)
    HDP->>HKS: UpsertAsync(batch) (寫 SQLite)

    ORC->>OPT: RunAsync(ranges, runOne)
    loop 每組參數 (Fast×Slow)
        OPT->>ENG: RunAsync(options, config)
        ENG-->>OPT: BacktestReport
        OPT->>BUS: RaiseOptimizationProgress(done/total, params)
        BUS-->>HUB: SendAsync("OptimizationProgress")
        BUS-->>ST: 直接 c# event
        ST-->>U: StateChanged → 進度條 + ETA
    end

    OPT-->>ORC: List<OptimizationRun>
    ORC->>BUS: RaiseOptimizationCompleted(rankedRows)
    BUS-->>ST: Leaderboard 就位
    ST-->>U: fade-in Leaderboard 表格

    U->>API: 點某列「套用」<br/>POST /api/lab/apply/{strategyId}
    API->>ORC: Stop → UpdateConfiguration → SaveChanges → Start (hot-swap)
    API-->>U: 200 OK
```

## 3. ListenKey 生命週期（補充細節）

```mermaid
stateDiagram-v2
    [*] --> Idle
    Idle --> Subscribing: StartAsync
    Subscribing --> Active: GetListenKeyAsync<br/>+ SubscribeToUserDataUpdatesAsync
    Active --> Active: 每 30 分 PUT /userDataStream<br/>(ExtendListenKeyAsync)
    Active --> Expired: BingXListenKeyExpiredUpdate
    Expired --> Idle: 清 _activeListenKey<br/>等上層 Stop→Start
    Active --> Stopping: StopAsync
    Stopping --> Idle: 取消續期 → 關 WS → DELETE listenKey
```

> 細節（30-min 而非 40-min、為何 expired 不自動重訂閱）見 `memory/reference_bingx_listenkey.md`。

## 4. 環境切換 (Demo ↔ Live) 資料流

S21 加入的熱切換會**中斷**上面第 1 節主流程，但保證不漏單、不混環境。下面是切換中的資料流快照：

```mermaid
sequenceDiagram
    autonumber
    participant UI as Blazor StatusBar
    participant L as LabStateContainer
    participant S as IEnvironmentSwitcher
    participant R as StrategyRuntimeHostedService
    participant E as BingXExchangeClient
    participant W as BingXMarketDataStream
    participant BX as BingX API

    Note over W,BX: 切換前：WS 持續接收 Demo 環境 tick
    UI->>L: ChangeEnvironmentAsync(Live)
    L->>S: SwitchAsync(Live)

    S->>R: StopAllAsync("env switch")
    R->>R: 每個 executor.StopAsync<br/>狀態 → Stopped
    Note over R: 此後不再有新訂單被送出

    S->>E: ReconfigureAsync(Live)
    E->>BX: Dispose old BingXRestClient<br/>Build new with Live endpoint
    Note over E: _clientGate lock 保證原子性

    S->>W: ReconfigureAsync(Live)
    W->>BX: rest.Reconfigure (defensive)<br/>Stop WS<br/>Build new BingXSocketClient<br/>(還沒訂閱任何東西)

    S->>W: StartAsync
    W->>BX: GetListenKey (Live env)<br/>SubscribeToUserDataUpdates (Live)
    Note over W,BX: 新 WS 在 Live 環境建立 listenKey

    S-->>L: raise EnvironmentChanged
    L-->>UI: StateChanged → MODE 變紅<br/>flash "Demo → Live (N stopped)"
    Note over UI: 策略不會自動重啟<br/>使用者必須手動重新 Start
```

**關鍵：** 整個流程中**沒有任何訂單被送出** — 因為 Runtime 在步驟 3 就全停了，直到使用者在 UI 手動重啟策略。這就是「金融安全」的具體實作。

## 5. 金鑰管理資料流 (S24)

Key 從使用者 UI 輸入到被 SDK 拿去打 REST call 的完整路徑：

```mermaid
sequenceDiagram
    autonumber
    participant U as 使用者
    participant ES as ExchangeSettings.razor<br/>(/settings/exchanges)
    participant API as /api/exchange-accounts
    participant Repo as IExchangeAccountRepository<br/>(EF · Scoped)
    participant DB as SQLite<br/>(ExchangeAccounts 表)
    participant Prov as IExchangeCredentialProvider<br/>(DbExchangeCredentialProvider · Singleton)
    participant BX as BingXExchangeClient<br/>(Singleton)
    participant BXAPI as BingX REST

    U->>ES: 填入 ApiKey / ApiSecret + Activate=true
    ES->>API: POST /api/exchange-accounts
    API->>Repo: AddAsync(account)
    API->>Repo: SetActiveAsync(id)<br/>（deactivate 兄弟）
    API->>Repo: uow.SaveChangesAsync
    Repo->>DB: INSERT / UPDATE ExchangeAccounts

    API->>Prov: NotifyCredentialsChangedAsync(BingX)
    Prov->>Repo: GetActiveAsync(BingX)
    Repo-->>Prov: ExchangeCredentials(IsConfigured=true)
    Note over Prov: fire CredentialsChanged event
    Prov->>BX: event handler<br/>ReconfigureCredentials(new key)
    Note over BX: lock(_clientGate)<br/>Dispose old BingXRestClient<br/>Build new(apiKey, apiSecret)

    API-->>ES: 201 Created
    ES-->>U: "已新增並啟用 — SDK client 已重建"

    Note over BX,BXAPI: 之後任何 REST call 都用新金鑰
    BX->>BXAPI: GET /balance / POST /order ...
```

### 關鍵保證

- **同交易所至多一筆 Active**：由 `SetActiveAsync` 在同一個 DB transaction 內 deactivate 其他帳號來保證。
- **同步事件 fire**：`NotifyCredentialsChangedAsync` 必須在返回前把所有 handler 跑完，否則 API response 到 UI 的時候 SDK 仍拿舊金鑰。
- **密文不回傳 UI**：`GET /api/exchange-accounts` 對 ApiKey 回 `first4…last4` 預覽、對 ApiSecret 一律 `••••••` 遮罩。
- **空字串保留原值**：`PUT /api/exchange-accounts/{id}` 以空字串送回 secret 不會覆寫 DB（Domain 層 `ExchangeAccount.UpdateCredentials` 保證）。

## 6. 策略大腦熱切換資料流 (S25)

Dashboard 下拉選單換「決策大腦」的完整路徑：

```mermaid
sequenceDiagram
    autonumber
    participant U as 使用者
    participant D as Dashboard.razor
    participant API as /api/strategies/{id}/type
    participant F as IStrategyFactory
    participant C as IStrategyRuntimeController<br/>(= StrategyRuntimeHostedService)
    participant E as 舊 IStrategyExecutor
    participant Repo as IStrategyRepository
    participant Ne as 新 IStrategyExecutor

    U->>D: 下拉選「B46RsiBb」(原本 SmaCrossover)
    D->>API: PUT { "strategyType": "B46RsiBb" }
    API->>F: KnownTypes.Contains("B46RsiBb")?
    F-->>API: true
    API->>C: ChangeStrategyTypeAsync(id, "B46RsiBb")

    Note over C: await _mutateLock
    C->>F: Get("B46RsiBb") → 預檢類型存在
    C->>E: StopAsync()（若在跑）
    E-->>C: stopped
    Note over C: _executors.Remove(id)

    C->>Repo: strategy.Stop("Type change in progress")
    C->>Repo: strategy.ChangeType("B46RsiBb")
    C->>Repo: strategy.Start()（若原本 Running）
    C->>Repo: uow.SaveChangesAsync

    alt 原本在跑
        C->>Ne: executorFactory.Create(strategy, impl)
        C->>Ne: StartAsync()
        Note over C: _executors[id] = Ne
    end

    C-->>API: true
    API->>Repo: 重拉最新狀態
    API-->>D: 200 { StrategyType: "B46RsiBb", Status: "Running" }
    D-->>U: 下拉 + Toggle 與 DB 一致
```

### 6.1 Toggle 路徑（更短）

```mermaid
sequenceDiagram
    autonumber
    participant U as 使用者
    participant D as Dashboard.razor
    participant API as /api/strategies/{id}/toggle
    participant C as IStrategyRuntimeController
    participant Repo as IStrategyRepository

    U->>D: 點 Running/Stopped Toggle
    D->>API: POST
    API->>Repo: GetByIdAsync(id)
    API->>C: Status == Running ? StopAsync : StartAsync
    alt Start
        C->>Repo: strategy.Start() + SaveChanges
        C->>C: executorFactory.Create + executor.StartAsync
    else Stop
        C->>C: executor.StopAsync + remove
        C->>Repo: strategy.Stop("Stopped via API") + SaveChanges
    end
    C-->>API: bool
    API-->>D: ToggleResponseDto
    D->>D: LoadStrategyControlsAsync()（重拉）
    D-->>U: Toggle 反映最新狀態
```

### 6.2 熱切換安全線

| 動作 | 是否停 executor | 是否改 DB Status | 是否改 Strategy.StrategyType | 是否重建 executor |
|---|---|---|---|---|
| **Toggle → Start** | — | Stopped → Running | — | Yes (新) |
| **Toggle → Stop** | Yes | Running → Stopped | — | — |
| **ChangeType（Running 中）** | Yes | Running → Stopped →（type 換完）→ Running | Yes | Yes (新類型) |
| **ChangeType（Stopped 中）** | — | Stopped（不動） | Yes | — |

Domain 層 `Strategy.ChangeType` 拒絕當 `Status == Running` 時被直接呼叫 — 上表的 Running-中 換腦動作**必須**由 Controller 先翻 Stopped 再換類型。任何繞過 Controller 直接在 Application service 裡改 Type 的程式碼一律違憲。

## 7. 重點不變式

- **WS → Application 必經 Infrastructure 翻譯**：Application 看到的永遠是 `Kline` / `MarketSnapshot` / `Position`，不是 `BingXFuturesAccountUpdate`。
- **DB 寫入永遠走 Repository 介面**，沒有任何路徑直接 `dbContext.SaveChanges()` 跳過抽象。
- **推播是雙通道**：本機 Blazor 走 `DashboardEventBus`（in-process），外部 client 走 SignalR — 兩者由 Orchestrator 同步觸發。
- **回測完全離線**：BacktestEngine 不碰任何外部 API，所有資料來自 `IHistoricalKlineStore`。
- **環境切換 Stop-First**：`IEnvironmentSwitcher.SwitchAsync` 永遠先 `StopAllAsync` 再 `ReconfigureAsync`，順序不可重排。
- **金鑰唯一來源 = SQLite**：Application 層程式碼不得再從 `IConfiguration` 讀 `BingX:ApiKey`；一律透過 `IExchangeCredentialProvider`。
- **Running 中不得換腦**：Domain 層 `Strategy.ChangeType` 強制先 Stop。UI 換型走 Controller 的 Stop-Then-Start 流程。
