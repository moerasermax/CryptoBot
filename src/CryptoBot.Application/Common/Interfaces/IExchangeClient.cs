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

    /// <summary>
    /// S66-D：查詢交易所伺服器目前 UTC 時間，用於偵測本地時鐘漂移。
    /// 由 <c>NtpDriftMonitor</c> 每 5 分鐘呼叫一次計算 <c>Offset = ServerTime − LocalTime</c>。
    /// 偏差超過 1000ms 將觸發 <c>RiskManager</c> 攔截，防止簽章失效或行情數據污染。
    /// </summary>
    Task<DateTime> GetServerTimeAsync(CancellationToken ct = default);

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
    /// S66-A：以 <paramref name="clientOrderId"/> 查詢交易所端對應訂單的當前狀態，主用於
    /// 兩個情境：
    ///   * Live 下單時 SDK 回 <c>duplicate clientOrderId</c> — 必須查交易所實際狀態回寫本地，
    ///     避免「呼叫過交易所、但本地 process crash 沒落 DB」的鬼單。
    ///   * DiagnosticTool <c>s66a_check-order</c> 一鍵比對本地 vs 交易所。
    ///
    /// 找不到訂單時回 <c>null</c>，呼叫端自行決定是否視為錯誤。
    /// </summary>
    Task<ExchangeOrderSnapshot?> GetOrderByClientOrderIdAsync(
        Symbol symbol, string clientOrderId, CancellationToken ct = default);

    /// <summary>
    /// S61：查詢交易所上當前**活躍**（未完全成交 / 未取消）的掛單，用於本地 vs 雲端對帳、
    /// 偵測幽靈訂單（雲端有、本地無）或本地殭屍訂單（本地有、雲端無）。
    /// 回傳僅含 <see cref="OrderStatus.New"/> / <see cref="OrderStatus.PartiallyFilled"/> 的條目。
    /// </summary>
    Task<IReadOnlyList<ExchangeOpenOrderInfo>> GetOpenOrdersAsync(Symbol symbol, CancellationToken ct = default);

    // ===== 持倉 =====

    /// <summary>取得交易所側的所有持倉 (用於對帳)</summary>
    Task<IReadOnlyList<ExchangePositionInfo>> GetOpenPositionsAsync(CancellationToken ct = default);

    /// <summary>
    /// S72：查詢交易所側的歷史成交明細，用於「實證對帳」— 當本地 Open Position 在遠端
    /// <see cref="GetOpenPositionsAsync"/> 查無時，必須以此方法查實際成交記錄取證，
    /// 嚴禁用 <see cref="GetMarkPriceAsync"/> 推算「假設成交價」結算 RealizedPnL（IM §S72 鐵則）。
    ///
    /// 找不到對應成交（Unaccounted）時呼叫端應採隱式約定（IsClosed=1 + RealizedPnL=0 + 廣播 [CRITICAL_SYNC]），
    /// 而非用行情價填充。
    /// </summary>
    /// <param name="symbol">過濾單一交易對。</param>
    /// <param name="since">起始時間（UTC，含）— 通常傳 Position.OpenedAt。</param>
    /// <param name="until">結束時間（UTC，含），null 代表「至今」。</param>
    Task<IReadOnlyList<ExchangeTradeInfo>> GetTradeHistoryAsync(
        Symbol symbol, DateTime since, DateTime? until = null, CancellationToken ct = default);
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

/// <summary>
/// S66-A：交易所端任意狀態（包含已成交/已取消）的訂單快照，由
/// <see cref="IExchangeClient.GetOrderByClientOrderIdAsync"/> 回傳。
/// 與 <see cref="ExchangeOpenOrderInfo"/> 不同：後者只列「活躍」掛單；本記錄涵蓋所有狀態。
/// </summary>
public sealed record ExchangeOrderSnapshot(
    string ExchangeOrderId,
    string ClientOrderId,
    Symbol Symbol,
    OrderSide Side,
    PositionSide PositionSide,
    OrderStatus Status,
    decimal Quantity,
    decimal QuantityFilled,
    decimal? AveragePrice,
    DateTime UpdateTime);

/// <summary>
/// S72：交易所端的單筆成交明細（fill / trade），由 <see cref="IExchangeClient.GetTradeHistoryAsync"/> 回傳。
/// 用於 AccountSynchronizer 對「本地 Open / 遠端不存在」的 orphan position 做實證對帳，
/// 加權平均 <see cref="Price"/> 後才能結算 RealizedPnL（嚴禁以行情 MarkPrice 推算）。
/// </summary>
/// <param name="TradeId">成交 ID（交易所側唯一）。</param>
/// <param name="OrderId">所屬訂單的 ExchangeOrderId — 對映本地 Order.ExchangeOrderId。</param>
/// <param name="Symbol">交易對。</param>
/// <param name="Side">買 / 賣方向（成交方向）。</param>
/// <param name="PositionSide">部位方向（Long / Short）— 用於對帳時匹配本地 Position.Side。</param>
/// <param name="Quantity">成交數量（base asset）。</param>
/// <param name="Price">成交價（quote asset / base asset）。</param>
/// <param name="Commission">手續費（負值代表支出）。</param>
/// <param name="RealizedPnl">交易所側已實現損益（若 SDK 不提供則為 0；以呼叫端的加權計算為準）。</param>
/// <param name="Time">成交時間（UTC）。</param>
public sealed record ExchangeTradeInfo(
    string TradeId,
    string OrderId,
    Symbol Symbol,
    OrderSide Side,
    PositionSide PositionSide,
    decimal Quantity,
    decimal Price,
    decimal Commission,
    decimal RealizedPnl,
    DateTime Time);
