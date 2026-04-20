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

## 4. 重點不變式

- **WS → Application 必經 Infrastructure 翻譯**：Application 看到的永遠是 `Kline` / `MarketSnapshot` / `Position`，不是 `BingXFuturesAccountUpdate`。
- **DB 寫入永遠走 Repository 介面**，沒有任何路徑直接 `dbContext.SaveChanges()` 跳過抽象。
- **推播是雙通道**：本機 Blazor 走 `DashboardEventBus`（in-process），外部 client 走 SignalR — 兩者由 Orchestrator 同步觸發。
- **回測完全離線**：BacktestEngine 不碰任何外部 API，所有資料來自 `IHistoricalKlineStore`。
