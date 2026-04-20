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

    // ===== 帳戶 =====

    /// <summary>取得合約帳戶餘額 (USDT)</summary>
    Task<decimal> GetFuturesBalanceAsync(string asset = "USDT", CancellationToken ct = default);

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
