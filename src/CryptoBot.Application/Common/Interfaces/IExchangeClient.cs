using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Common.Interfaces;

/// <summary>
/// 交易所抽象介面 - Clean Architecture 的關鍵。
/// Application 層只依賴此介面，不知道底層是 BingX 還是其他交易所。
/// 將來要擴充 Binance/OKX 只需要實作此介面。
/// </summary>
public interface IExchangeClient
{
    /// <summary>交易所名稱 (用於 Log / 識別)</summary>
    string ExchangeName { get; }

    /// <summary>
    /// 此 client 當前模式對應的合約 quote 資產：Live→"USDT", Demo→"VST"。
    /// <see cref="GetFuturesBalanceAsync"/> 不傳 asset 時會用這個。
    /// </summary>
    string QuoteAsset { get; }

    /// <summary>當前模式 — 提供給 UI / EnvironmentSwitcher 觀察用。</summary>
    TradingMode CurrentMode { get; }

    /// <summary>
    /// 熱切換交易模式（Demo ↔ Live）。
    /// 實作必須：(1) 內部上鎖避免 in-flight REST/WS 操作觀察到半切狀態；
    /// (2) Dispose 舊 SDK client、用新 endpoint 重建；(3) 重建後 <see cref="QuoteAsset"/>
    /// 與 <see cref="CurrentMode"/> 立刻反映新模式。
    /// 切換後若呼叫端有訂單 / 持倉狀態快取，必須自行清掉 — 兩個環境不共享資料。
    /// </summary>
    Task ReconfigureAsync(TradingMode newMode, CancellationToken ct = default);

    // ===== 帳戶 =====

    /// <summary>
    /// 取得合約帳戶餘額。傳 null（預設）時會用 <see cref="QuoteAsset"/>，
    /// 也就是「當前模式正確的那個資產」— 永遠不會在 demo 跑去查 USDT。
    /// </summary>
    Task<decimal> GetFuturesBalanceAsync(string? asset = null, CancellationToken ct = default);

    /// <summary>取得現貨帳戶餘額 (用於對沖套利)</summary>
    Task<decimal> GetSpotBalanceAsync(string asset, CancellationToken ct = default);

    /// <summary>設定槓桿</summary>
    Task SetLeverageAsync(Symbol symbol, Leverage leverage, CancellationToken ct = default);

    /// <summary>設定保證金模式</summary>
    Task SetMarginModeAsync(Symbol symbol, MarginMode mode, CancellationToken ct = default);

    // ===== 行情 =====

    Task<IReadOnlyList<Kline>> GetKlinesAsync(
        Symbol symbol,
        KlineInterval interval,
        int limit = 500,
        DateTime? startTime = null,
        DateTime? endTime = null,
        CancellationToken ct = default);

    Task<Price> GetMarkPriceAsync(Symbol symbol, CancellationToken ct = default);

    Task<Price> GetSpotPriceAsync(Symbol symbol, CancellationToken ct = default);

    Task<MarketSnapshot> GetMarketSnapshotAsync(Symbol symbol, CancellationToken ct = default);

    /// <summary>取得合約交易規格 (最小下單量、精度等)</summary>
    Task<SymbolTradingRules> GetTradingRulesAsync(Symbol symbol, CancellationToken ct = default);

    // ===== 下單 =====

    /// <summary>
    /// 下單到交易所。成功後 order 會被綁定 ExchangeOrderId。
    /// </summary>
    Task PlaceOrderAsync(Order order, CancellationToken ct = default);

    /// <summary>取消訂單</summary>
    Task CancelOrderAsync(Order order, CancellationToken ct = default);

    /// <summary>查詢訂單狀態 (成交進度)</summary>
    Task RefreshOrderStatusAsync(Order order, CancellationToken ct = default);

    /// <summary>
    /// S61：查詢交易所上當前**活躍**（未完全成交 / 未取消）的掛單，用於本地 vs 雲端對帳、
    /// 偵測幽靈訂單（雲端有、本地無）或本地殭屍訂單（本地有、雲端無）。
    /// 回傳僅含 <see cref="OrderStatus.New"/> / <see cref="OrderStatus.PartiallyFilled"/> 的條目。
    /// </summary>
    Task<IReadOnlyList<ExchangeOpenOrderInfo>> GetOpenOrdersAsync(Symbol symbol, CancellationToken ct = default);

    // ===== 持倉 =====

    /// <summary>取得交易所側的所有持倉 (用於對帳)</summary>
    Task<IReadOnlyList<ExchangePositionInfo>> GetOpenPositionsAsync(CancellationToken ct = default);
}

/// <summary>
/// 交易規則 - 從交易所查詢到的限制
/// </summary>
public sealed record SymbolTradingRules(
    Symbol Symbol,
    decimal MinQuantity,
    decimal MaxQuantity,
    decimal StepSize,
    decimal TickSize,
    decimal MinNotional,
    int MaxLeverage);

/// <summary>
/// 交易所側的持倉資訊 (用於對帳用)
/// </summary>
public sealed record ExchangePositionInfo(
    Symbol Symbol,
    PositionSide Side,
    decimal Quantity,
    decimal EntryPrice,
    decimal MarkPrice,
    decimal UnrealizedPnL,
    decimal LiquidationPrice,
    int Leverage);

/// <summary>
/// S61：交易所側的活躍訂單資訊 — 用於 Ghost Order Inspector 的本地 vs 雲端對帳。
/// ExchangeOrderId 是雙方共通的主鍵。
/// </summary>
public sealed record ExchangeOpenOrderInfo(
    string ExchangeOrderId,
    Symbol Symbol,
    OrderSide Side,
    PositionSide PositionSide,
    OrderStatus Status,
    decimal Quantity,
    decimal QuantityFilled,
    decimal? Price,
    DateTime UpdateTime);
