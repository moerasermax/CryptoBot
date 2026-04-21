# UML · Core Domain Class Diagram

> 涵蓋 Domain 核心 Aggregate / Value Object / Domain Service，加上 Application 層的 `IStrategy` 與 Lab 的策略插槽契約 (`StrategyParameterFormBase` / `StrategyCatalog` / `OptimizationRequest`)。

## 1. Domain Aggregates + Value Objects + IStrategy

```mermaid
classDiagram
    direction LR

    %% ─── Aggregates (root underlined by stereotype) ───
    class Strategy {
        <<AggregateRoot>>
        +Guid Id
        +string Name
        +StrategyType Type
        +StrategyStatus Status
        +StrategyConfiguration Configuration
        +Start() void
        +Stop() void
        +UpdateConfiguration(StrategyConfiguration cfg) void
    }

    class StrategyConfiguration {
        <<ValueObject>>
        +Symbol Symbol
        +KlineInterval Interval
        +Leverage Leverage
        +int MaxKlineWindow
        +IReadOnlyDictionary~string,decimal~ Parameters
        +Create(...) StrategyConfiguration
        +GetParameter(string key) decimal
    }

    class TradingSignal {
        <<ValueObject>>
        +SignalAction Action
        +Price? EntryPrice
        +Quantity? Quantity
        +string Reason
        +Hold() TradingSignal
        +Buy(...) TradingSignal
        +Sell(...) TradingSignal
    }

    class Order {
        <<AggregateRoot>>
        +Guid Id
        +Symbol Symbol
        +OrderSide Side
        +PositionSide PositionSide
        +OrderType Type
        +OrderStatus Status
        +Quantity Quantity
        +Quantity FilledQuantity
        +Price? Price
        +Price? AveragePrice
        +DateTime CreatedAt
        +Fill(Quantity, Price) void
        +Cancel() void
    }

    class Position {
        <<AggregateRoot>>
        +Guid Id
        +Symbol Symbol
        +PositionSide Side
        +Quantity Size
        +Price EntryPrice
        +Money UnrealizedPnL
        +UpdateUnrealized(Price markPrice) void
    }

    class Kline {
        <<ValueObject>>
        +Symbol Symbol
        +KlineInterval Interval
        +DateTime OpenTime
        +Price Open / High / Low / Close
        +decimal Volume
    }

    class MarketSnapshot {
        <<ValueObject>>
        +Symbol Symbol
        +Price LastPrice
        +Price MarkPrice
        +DateTime Timestamp
    }

    %% ─── Value Objects ───
    class Symbol { <<ValueObject>> +string Value +string BingXFormat +Parse(string) Symbol }
    class Price { <<ValueObject>> +decimal Value }
    class Quantity { <<ValueObject>> +decimal Value }
    class Money { <<ValueObject>> +decimal Amount +string Currency }
    class Leverage { <<ValueObject>> +int Value +Conservative$ Leverage +Aggressive$ Leverage }

    %% ─── Domain Service ───
    class PositionSizingService {
        <<DomainService>>
        +CalculateSize(Money equity, Price entry, Price stop, Leverage) Quantity
    }

    %% ─── Application Layer Interface ───
    class IStrategy {
        <<interface>>
        +string StrategyType
        +AnalyzeAsync(StrategyConfiguration, IReadOnlyList~Kline~, MarketSnapshot, IReadOnlyList~Position~, CancellationToken) Task~TradingSignal~
    }

    class SmaCrossoverStrategy {
        +string StrategyType
        +AnalyzeAsync(...) Task~TradingSignal~
    }

    %% ─── Relationships ───
    Strategy "1" *-- "1" StrategyConfiguration : holds
    Strategy ..> TradingSignal : produces (via IStrategy)
    Order "*" --> "1" Symbol
    Position "*" --> "1" Symbol
    Kline --> Symbol
    MarketSnapshot --> Symbol
    Order --> Quantity
    Order --> Price
    Position --> Money
    StrategyConfiguration --> Symbol
    StrategyConfiguration --> Leverage
    PositionSizingService ..> Money : uses
    PositionSizingService ..> Leverage : uses
    SmaCrossoverStrategy ..|> IStrategy
    IStrategy ..> StrategyConfiguration : reads
    IStrategy ..> Kline : reads
    IStrategy ..> Position : reads
    IStrategy ..> TradingSignal : returns
```

## 2. Strategy Slot 協議（Lab 插槽契約）

```mermaid
classDiagram
    direction LR

    class StrategyCatalog {
        -List~StrategyModel~ _models
        +IReadOnlyList~StrategyModel~ Models
        +StrategyModel Default
        +FindByKey(string key) StrategyModel?
        -Register(StrategyModel) void
    }

    class StrategyModel {
        <<record>>
        +string Key
        +string DisplayName
        +string Subtitle
        +Type? FormComponent
        +bool IsLocked
    }

    class LabStateContainer {
        <<Singleton>>
        +StrategyModel SelectedModel
        +OptimizationProgressUpdate? Progress
        +OptimizationCompletedUpdate? Leaderboard
        +string? LastError
        +bool IsRunning
        +TimeSpan? Elapsed
        +TimeSpan? EstimatedRemaining
        +event Action StateChanged
        +SelectModel(string key) void
        +NotifyJobStarting(int total) void
    }

    class StrategyParameterFormBase {
        <<abstract ComponentBase>>
        +bool Disabled
        +EventCallback~int~ ParameterChanged
        +int CurrentGridSize *
        +BuildRequest(DateTime, DateTime, out string?) OptimizationRequest? *
        #NotifyChangedAsync() Task
    }

    class SmaParameterForm {
        -decimal _fastMin / _fastMax / _fastStep
        -decimal _slowMin / _slowMax / _slowStep
        +int CurrentGridSize
        +BuildRequest(...) OptimizationRequest?
    }

    class OptimizationRequest {
        <<record>>
        +decimal FastMin / FastMax / FastStep
        +decimal SlowMin / SlowMax / SlowStep
        +DateTime StartUtc
        +DateTime EndUtc
    }

    class OptimizationOrchestrator {
        <<Singleton>>
        -SemaphoreSlim _gate
        +bool IsRunning
        +TryStart(OptimizationRequest) bool
        -RunAsync(...) Task
    }

    class DashboardEventBus {
        +event Action~OptimizationProgressUpdate~ OptimizationProgress
        +event Action~OptimizationCompletedUpdate~ OptimizationCompleted
        +event Action~OptimizationFailedUpdate~ OptimizationFailed
        +RaiseOptimizationProgress(...) void
        +RaiseOptimizationCompleted(...) void
        +RaiseOptimizationFailed(...) void
    }

    StrategyCatalog "1" *-- "*" StrategyModel
    StrategyModel ..> StrategyParameterFormBase : FormComponent typeof
    SmaParameterForm --|> StrategyParameterFormBase
    LabStateContainer "1" --> "1" StrategyCatalog : default model
    LabStateContainer ..> DashboardEventBus : subscribes
    OptimizationOrchestrator ..> DashboardEventBus : raises
    OptimizationOrchestrator ..> OptimizationRequest : consumes
    StrategyParameterFormBase ..> OptimizationRequest : builds
```

## 3. 環境熱切換契約 (S21)

```mermaid
classDiagram
    direction LR

    class TradingMode {
        <<enum · Application.Common>>
        Demo = 0
        Live = 1
    }

    class IEnvironmentSwitcher {
        <<interface · Application.Common>>
        +TradingMode CurrentMode
        +event Action~EnvironmentChangedEvent~ EnvironmentChanged
        +SwitchAsync(TradingMode newMode, string? reason, CancellationToken) Task~EnvironmentSwitchResult~
    }

    class EnvironmentSwitcher {
        <<sealed · Singleton>>
        -SemaphoreSlim _switchLock
        -IExchangeClient _exchange
        -IMarketDataStream _marketData
        -IStrategyRuntimeController _runtime
        +SwitchAsync(...) Task~EnvironmentSwitchResult~
    }

    class EnvironmentSwitchResult {
        <<record>>
        +TradingMode FromMode
        +TradingMode ToMode
        +IReadOnlyList~Guid~ StoppedStrategyIds
        +DateTime SwitchedAtUtc
        +string? Reason
    }

    class EnvironmentChangedEvent {
        <<record>>
        +TradingMode FromMode
        +TradingMode ToMode
        +IReadOnlyList~Guid~ StoppedStrategyIds
        +DateTime ChangedAtUtc
        +string? Reason
    }

    class IExchangeClient {
        <<interface · Application.Common.Interfaces>>
        +string ExchangeName
        +string QuoteAsset
        +TradingMode CurrentMode
        +ReconfigureAsync(TradingMode newMode, CancellationToken) Task
        +GetFuturesBalanceAsync(string? asset, CancellationToken) Task~decimal~
        +PlaceOrderAsync(Order, CancellationToken) Task
        ...
    }

    class IMarketDataStream {
        <<interface · IAsyncDisposable>>
        +StartAsync(CancellationToken) Task
        +StopAsync(CancellationToken) Task
        +ReconfigureAsync(TradingMode newMode, CancellationToken) Task
        +event OnKlineUpdate
        +event OnExchangeOrderUpdate
        +event OnExchangeAccountUpdate
        ...
    }

    class IStrategyRuntimeController {
        <<interface>>
        +IReadOnlyList~Guid~ RunningStrategyIds
        +bool IsRunning(Guid strategyId)
        +Task~Guid~ StartAsync(Strategy, CancellationToken)
        +Task StopAsync(Guid strategyId, string reason, CancellationToken)
        +Task~IReadOnlyList~Guid~~ StopAllAsync(string reason, CancellationToken)
    }

    class BingXExchangeClient {
        <<sealed · Singleton>>
        -object _clientGate
        -BingXRestClient _client
        -BingXOptions _options
        +TradingMode CurrentMode
        +ReconfigureAsync(...) Task
        -BuildRestClient(TradingMode) BingXRestClient
        -Snapshot() BingXRestClient
    }

    class BingXMarketDataStream {
        <<sealed · Singleton>>
        -object _clientGate
        -BingXSocketClient _socketClient
        -bool _started
        +ReconfigureAsync(...) Task
        -BuildSocketClient(TradingMode) BingXSocketClient
    }

    class BingXOptions {
        <<mutable POCO>>
        +string ApiKey
        +TradingMode TradingMode
        +bool UseDemoTrading
        +TradingMode EffectiveMode «derived»
        +string QuoteAsset «derived»
    }

    class LabStateContainer {
        <<Singleton>>
        +TradingMode CurrentMode
        +EnvironmentChangedEvent? LastEnvChange
        +ChangeEnvironmentAsync(TradingMode, string?, CancellationToken) Task~EnvironmentSwitchResult~
        +event Action StateChanged
    }

    EnvironmentSwitcher ..|> IEnvironmentSwitcher
    EnvironmentSwitcher ..> IExchangeClient : Reconfigure
    EnvironmentSwitcher ..> IMarketDataStream : Reconfigure + Start
    EnvironmentSwitcher ..> IStrategyRuntimeController : StopAllAsync
    EnvironmentSwitcher ..> EnvironmentSwitchResult : returns
    EnvironmentSwitcher ..> EnvironmentChangedEvent : raises
    IEnvironmentSwitcher ..> TradingMode
    IExchangeClient ..> TradingMode
    IMarketDataStream ..> TradingMode
    BingXExchangeClient ..|> IExchangeClient
    BingXMarketDataStream ..|> IMarketDataStream
    BingXExchangeClient ..> BingXOptions : mutates
    BingXMarketDataStream ..> BingXOptions : reads
    LabStateContainer ..> IEnvironmentSwitcher : delegates
    LabStateContainer ..> EnvironmentChangedEvent : subscribes
```

### 設計亮點

| 設計 | 為什麼 |
|---|---|
| `TradingMode` 放在 `Application.Common` 而非 Infrastructure.Configuration | Application 介面（`IExchangeClient.CurrentMode`、`IMarketDataStream.ReconfigureAsync`）需要參照它 — 放 Infrastructure 會違反相依方向 |
| `IEnvironmentSwitcher` 在 Application 而非 Infrastructure | 編排服務跨 `IExchangeClient` + `IMarketDataStream` + `IStrategyRuntimeController` 三個 Application 介面，本身沒有任何 BingX 知識 |
| `BingXOptions` 變 mutable POCO，`EffectiveMode` 是衍生屬性 | `IOptions<T>` 是建構期 snapshot，切換後不更新；改 mutable + derived 才能讓 `CurrentMode` 立即反映 |
| `_clientGate` 物件鎖而非 `lock(this)` 或 `lock(_options)` | 專屬鎖避免外部偶然 lock 同一物件造成 deadlock；`Dispose` 舊 client 在 lock 外執行 |
| 切換後**不**自動重啟策略 | 強制使用者手動 Start = 二次人為確認，杜絕「demo 配置打 live 訂單」 |

## 4. 讀圖規則

- `*` 為 abstract / 必須 override 的成員。
- `<<...>>` 為 stereotype，標出該型別的角色（Aggregate / ValueObject / Singleton / record / interface）。
- 實線箭頭 = 組合 / 強相依；虛線箭頭 = 使用 / 訊息傳遞。
- 三角形空心箭頭 (`..|>`) = 介面實作。

## 5. 設計決策摘要

| 決策 | 為何 |
|---|---|
| `StrategyConfiguration.Parameters` 用 `Dictionary<string, decimal>` 而非 strong-typed 子類 | 換策略不必碰 Domain；`decimal` 維持金融精度 |
| `IStrategy` 簽章只吃 Domain 物件 | 純函式 → 可重放、可單測、不會被 IO 污染 |
| `OptimizationRequest` 是 record，欄位 = 兩維 SMA + 時間窗 | 對應目前唯一的 active 策略 SMA；新策略需擴充時改成 sealed hierarchy 或 polymorphic payload |
| `LabStateContainer` 是 Singleton 而非 Scoped | Blazor Server 多 circuit、刷新、跨 tab 都要看到同一份狀態；多人模式才需要改 Scoped |
