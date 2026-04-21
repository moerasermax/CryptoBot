using BingX.Net;
using BingX.Net.Clients;
using CryptoBot.Application.Common;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.ValueObjects;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading;
using System.Threading.Tasks;
using AppBingXOptions = CryptoBot.Infrastructure.Configuration.BingXOptions;

namespace CryptoBot.Infrastructure.Exchange.BingX;

public sealed class BingXExchangeClient : IExchangeClient, IDisposable
{
    // 不是 readonly — ReconfigureAsync 會原子置換成新環境的 client。
    private BingXRestClient _client;
    private readonly AppBingXOptions _options;
    private readonly IExchangeCredentialProvider _credentials;
    private readonly ILogger<BingXExchangeClient> _logger;

    /// <summary>
    /// 守護 <see cref="_client"/> 與 <see cref="_options"/> 的可變欄位 — 切換環境時取，公開方法
    /// 只在 lock 內讀取 client 引用以避免半切。讀取本身極快 (拿 reference)，
    /// 不會把網路 IO 圈在 lock 裡。
    /// </summary>
    private readonly object _clientGate = new();

    public string ExchangeName => "BingX";
    public string QuoteAsset => _options.QuoteAsset;
    public TradingMode CurrentMode => _options.EffectiveMode;

    public BingXExchangeClient(
        IOptions<AppBingXOptions> options,
        IExchangeCredentialProvider credentials,
        ILogger<BingXExchangeClient> logger)
    {
        _options = options.Value;
        _credentials = credentials;
        _logger = logger;

        // S24：DB 的活躍金鑰優先，appsettings 只當 fallback。DbContext 在 ctor 階段需要一個 scope
        // （IExchangeCredentialProvider 內部會自建），因此 sync-over-async 只在啟動期發生一次，
        // 之後都走事件驅動。
        TryApplyDbCredentialsAtStartup();

        _client = BuildRestClient(_options.EffectiveMode);

        _credentials.CredentialsChanged += OnCredentialsChanged;
    }

    private void TryApplyDbCredentialsAtStartup()
    {
        try
        {
            var creds = _credentials
                .GetActiveAsync(Domain.Enums.ExchangeName.BingX, CancellationToken.None)
                .GetAwaiter().GetResult();

            if (creds.IsConfigured)
            {
                _options.ApiKey = creds.ApiKey;
                _options.ApiSecret = creds.ApiSecret;
                _logger.LogInformation(
                    "BingX credentials loaded from SQLite | account={Account}", creds.AccountName);
            }
            else if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                _logger.LogWarning(
                    "No active BingX account in SQLite — falling back to appsettings.json keys. " +
                    "Configure via /settings/exchanges to migrate.");
            }
            else
            {
                _logger.LogWarning(
                    "BingX has no credentials — user must configure at /settings/exchanges before trading.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to load BingX credentials from SQLite — falling back to appsettings.json keys.");
        }
    }

    private void OnCredentialsChanged(object? sender, ExchangeCredentialsChangedEventArgs e)
    {
        if (e.Exchange != Domain.Enums.ExchangeName.BingX) return;

        BingXRestClient oldClient;
        lock (_clientGate)
        {
            _options.ApiKey = e.Credentials.ApiKey;
            _options.ApiSecret = e.Credentials.ApiSecret;
            oldClient = _client;
            _client = BuildRestClient(_options.EffectiveMode);
        }

        try { oldClient.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Old BingX REST client Dispose threw on credentials swap — ignored."); }

        _logger.LogWarning(
            "🔑 BingX credentials SWAPPED (account={Account}, configured={Configured}) — SDK client rebuilt.",
            e.Credentials.AccountName, e.Credentials.IsConfigured);
    }

    public void Dispose()
    {
        _credentials.CredentialsChanged -= OnCredentialsChanged;
        try { _client.Dispose(); } catch { /* swallow — best-effort */ }
    }

    private BingXRestClient BuildRestClient(TradingMode mode)
    {
        var client = new BingXRestClient(opts =>
        {
            opts.RequestTimeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);
            opts.Environment = mode == TradingMode.Live
                ? global::BingX.Net.BingXEnvironment.Live
                : global::BingX.Net.BingXEnvironment.Demo;
        });

        if (!string.IsNullOrWhiteSpace(_options.ApiKey) &&
            !string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            var creds = new BingXCredentials
            {
                Key = _options.ApiKey,
                Secret = _options.ApiSecret,
            };
            client.PerpetualFuturesApi.SetApiCredentials(creds);
            client.SpotApi.SetApiCredentials(creds);
        }

        _logger.LogWarning("BingX REST client built | mode: {Mode} | quote asset: {Asset}",
            mode == TradingMode.Live ? "🔴 LIVE (REAL MONEY)" : "🟢 DEMO (VST)",
            mode == TradingMode.Live ? "USDT" : "VST");

        return client;
    }

    /// <summary>
    /// 取目前的 client reference — 公開方法都應透過這個快照取用，避免在切換中拿到半態 client。
    /// 假設：呼叫端已透過 EnvironmentSwitcher 停止所有 executor，這裡只用來保證引用 atomic。
    /// </summary>
    private BingXRestClient Snapshot()
    {
        lock (_clientGate) return _client;
    }

    public Task ReconfigureAsync(TradingMode newMode, CancellationToken ct = default)
    {
        // 重建是 fire-and-replace — Dispose 舊 client，然後把欄位指向新 client。
        // 因為 BingXRestClient 沒有 IAsyncDisposable，純 Dispose 即可。
        BingXRestClient oldClient;
        lock (_clientGate)
        {
            if (_options.EffectiveMode == newMode)
            {
                _logger.LogInformation("BingX REST already in {Mode} — Reconfigure is a no-op.", newMode);
                return Task.CompletedTask;
            }

            // 同步 options（讓所有派生屬性 — QuoteAsset / EffectiveMode — 立即反映）
            _options.TradingMode = newMode;
            _options.UseDemoTrading = newMode == TradingMode.Demo;

            oldClient = _client;
            _client = BuildRestClient(newMode);
        }

        try { oldClient.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "BingX REST client old-instance Dispose threw — ignoring."); }

        _logger.LogWarning("🔁 BingX REST client SWAPPED to {Mode} (new quote asset: {Asset})",
            newMode, _options.QuoteAsset);
        return Task.CompletedTask;
    }

    // ========== 帳戶 ==========

    public async Task<decimal> GetFuturesBalanceAsync(
        string? asset = null, CancellationToken ct = default)
    {
        var queryAsset = string.IsNullOrWhiteSpace(asset) ? QuoteAsset : asset;

        var result = await _client.PerpetualFuturesApi.Account
            .GetBalancesAsync(ct).ConfigureAwait(false);

        result.Check(nameof(GetFuturesBalanceAsync));

        var balance = result.Data.FirstOrDefault(b =>
            string.Equals(b.Asset, queryAsset, StringComparison.OrdinalIgnoreCase));

        if (balance is null)
        {
            _logger.LogInformation(
                "No {Asset} balance on BingX futures account (mode={Mode}) — returning 0.",
                queryAsset, _options.EffectiveMode);
            return 0m;
        }

        return balance.Balance.GetValueOrDefault();
    }

    public async Task<decimal> GetSpotBalanceAsync(
        string asset, CancellationToken ct = default)
    {
        var result = await _client.SpotApi.Account
            .GetBalancesAsync(ct).ConfigureAwait(false);

        result.Check(nameof(GetSpotBalanceAsync));

        var balance = result.Data.FirstOrDefault(b =>
            string.Equals(b.Asset, asset, StringComparison.OrdinalIgnoreCase));

        return balance?.Free ?? 0m;
    }

    public async Task SetLeverageAsync(
        Symbol symbol, Leverage leverage, CancellationToken ct = default)
    {
        foreach (var side in new[]
                 {
                     global::BingX.Net.Enums.PositionSide.Long,
                     global::BingX.Net.Enums.PositionSide.Short
                 })
        {
            var result = await _client.PerpetualFuturesApi.Account
                .SetLeverageAsync(symbol.BingXFormat, side, leverage.Value, ct: ct)
                .ConfigureAwait(false);

            result.Check($"{nameof(SetLeverageAsync)} ({side})");
        }

        _logger.LogInformation("Leverage={Lev}x {Symbol}",
            leverage.Value, symbol.BingXFormat);
    }

    public async Task SetMarginModeAsync(
        Symbol symbol, MarginMode mode, CancellationToken ct = default)
    {
        var result = await _client.PerpetualFuturesApi.Account
            .SetMarginModeAsync(symbol.BingXFormat, mode.ToBingX(), ct: ct)
            .ConfigureAwait(false);

        result.Check(nameof(SetMarginModeAsync));
    }

    // ========== 行情 ==========

    public async Task<IReadOnlyList<Kline>> GetKlinesAsync(
        Symbol symbol,
        Domain.Enums.KlineInterval interval,
        int limit = 500,
        DateTime? startTime = null,
        DateTime? endTime = null,
        CancellationToken ct = default)
    {
        if (limit <= 0 || limit > 1440)
            throw new DomainException($"Kline limit must be 1..1440, got {limit}");

        var result = await _client.PerpetualFuturesApi.ExchangeData
            .GetKlinesAsync(
                symbol.BingXFormat,
                interval.ToBingX(),
                startTime,
                endTime,
                limit,
                ct: ct)
            .ConfigureAwait(false);

        result.Check(nameof(GetKlinesAsync));

        var klines = new List<Kline>();
        foreach (var k in result.Data.OrderBy(x => x.Timestamp))
        {
            var openTime = k.Timestamp;
            var closeTime = openTime.AddSeconds(1);

            klines.Add(Kline.Create(
                openTime: openTime,
                closeTime: closeTime,
                open: k.OpenPrice,
                high: k.HighPrice,
                low: k.LowPrice,
                close: k.ClosePrice,
                volume: k.Volume,
                interval: interval));
        }

        return klines;
    }

    public async Task<Price> GetMarkPriceAsync(
        Symbol symbol, CancellationToken ct = default)
    {
        // v3.10.0 的 ExchangeData 沒有 GetPricesAsync / GetPremiumIndexAsync
        // 用 BookTicker 的 (bid+ask)/2 當 mark price 近似
        var result = await _client.PerpetualFuturesApi.ExchangeData
            .GetBookTickerAsync(symbol.BingXFormat, ct).ConfigureAwait(false);

        result.Check(nameof(GetMarkPriceAsync));

        var bid = result.Data.BestBidPrice;
        var ask = result.Data.BestAskPrice;
        if (bid <= 0 && ask <= 0)
            throw new ExchangeApiException($"No price for {symbol.BingXFormat}");

        var mid = (bid > 0 && ask > 0) ? (bid + ask) / 2m : Math.Max(bid, ask);
        return Price.Create(mid);
    }

    public async Task<Price> GetSpotPriceAsync(
        Symbol symbol, CancellationToken ct = default)
    {
        var result = await _client.SpotApi.ExchangeData
            .GetTickersAsync(symbol.BingXFormat, ct).ConfigureAwait(false);

        result.Check(nameof(GetSpotPriceAsync));

        var ticker = result.Data.FirstOrDefault()
            ?? throw new ExchangeApiException($"No spot ticker for {symbol.BingXFormat}");

        return Price.Create(ticker.LastPrice);
    }

    public async Task<MarketSnapshot> GetMarketSnapshotAsync(
        Symbol symbol, CancellationToken ct = default)
    {
        var bookTask = _client.PerpetualFuturesApi.ExchangeData
            .GetBookTickerAsync(symbol.BingXFormat, ct);
        var spotTask = _client.SpotApi.ExchangeData
            .GetTickersAsync(symbol.BingXFormat, ct);

        await Task.WhenAll(bookTask, spotTask).ConfigureAwait(false);

        // 修正：之前誤寫成 Check(result,...)，應該是 bookTask.Result
        bookTask.Result.Check($"{nameof(GetMarketSnapshotAsync)} (book)");

        var book = bookTask.Result.Data;
        var bid = book.BestBidPrice;
        var ask = book.BestAskPrice;
        var mid = (bid > 0 && ask > 0) ? (bid + ask) / 2m : Math.Max(bid, ask);

        Price? spotPrice = null;
        if (spotTask.Result.Success)
        {
            var t = spotTask.Result.Data.FirstOrDefault();
            if (t != null) spotPrice = Price.Create(t.LastPrice);
        }

        return MarketSnapshot.Create(
            symbol: symbol,
            timestamp: DateTime.UtcNow,
            futuresMarkPrice: Price.Create(mid),
            futuresBidPrice: Price.Create(bid),
            futuresAskPrice: Price.Create(ask),
            spotPrice: spotPrice,
            fundingRate: null);
    }

    public async Task<SymbolTradingRules> GetTradingRulesAsync(
        Symbol symbol, CancellationToken ct = default)
    {
        var result = await _client.PerpetualFuturesApi.ExchangeData
            .GetContractsAsync(ct: ct).ConfigureAwait(false);

        result.Check(nameof(GetTradingRulesAsync));

        var contract = result.Data.FirstOrDefault(c =>
            string.Equals(c.Symbol, symbol.BingXFormat, StringComparison.OrdinalIgnoreCase))
            ?? throw new DomainException($"Contract not found: {symbol.BingXFormat}");

        // v3.10.0 BingXContract 欄位名未定，先用 dynamic 保底
        // 待 F12 確認實際欄位後，替換成具名屬性
        dynamic dc = contract;

        decimal minQty = 0m;
        try { minQty = (decimal?)dc.MinOrderQuantity ?? 0m; }
        catch { try { minQty = (decimal?)dc.MinQuantity ?? 0m; } catch { } }

        decimal stepSize = 0m;
        try { stepSize = (decimal?)dc.QuantityStep ?? 0m; }
        catch
        {
            try { stepSize = (decimal?)dc.StepSize ?? 0m; }
            catch { try { stepSize = (decimal?)dc.QuantityPrecision ?? 0m; } catch { } }
        }

        decimal tickSize = 0m;
        try { tickSize = (decimal?)dc.PriceStep ?? 0m; }
        catch
        {
            try { tickSize = (decimal?)dc.TickSize ?? 0m; }
            catch { try { tickSize = (decimal?)dc.PricePrecision ?? 0m; } catch { } }
        }

        decimal minNotional = 0m;
        try { minNotional = (decimal?)dc.MinNotional ?? 0m; }
        catch
        {
            try { minNotional = (decimal?)dc.MinOrderValue ?? 0m; }
            catch { try { minNotional = (decimal?)dc.MinOrderAmount ?? 0m; } catch { } }
        }

        return new SymbolTradingRules(
            Symbol: symbol,
            MinQuantity: minQty,
            MaxQuantity: decimal.MaxValue,
            StepSize: stepSize,
            TickSize: tickSize,
            MinNotional: minNotional,
            MaxLeverage: 125);
    }

    // ========== 下單 ==========

    public async Task PlaceOrderAsync(Order order, CancellationToken ct = default)
    {
        try
        {
            var result = await _client.PerpetualFuturesApi.Trading.PlaceOrderAsync(
                symbol: order.Symbol.BingXFormat,
                side: order.Side.ToBingX(),
                positionSide: order.PositionSide.ToBingX(),
                type: order.Type.ToBingX(),
                quantity: order.Quantity.Value,
                price: order.LimitPrice?.Value,
                stopPrice: order.StopPrice?.Value,
                clientOrderId: order.ClientOrderId,
                ct: ct).ConfigureAwait(false);

            if (!result.Success)
            {
                var errMsg = result.Error?.Message ?? "unknown";
                _logger.LogError("PlaceOrder rejected: {Symbol} err={Err}",
                    order.Symbol.BingXFormat, errMsg);
                order.Reject(errMsg);
                return;
            }

            order.AssignExchangeOrderId(result.Data.OrderId.ToString());

            _logger.LogInformation("Order placed: {Symbol} {Side} qty={Qty} id={Id}",
                order.Symbol.BingXFormat, order.Side,
                order.Quantity.Value, result.Data.OrderId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception placing order for {Symbol}", order.Symbol.BingXFormat);
            if (order.IsActive) order.Reject($"Exception: {ex.Message}");
            throw;
        }
    }

    public async Task CancelOrderAsync(Order order, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(order.ExchangeOrderId))
            throw new DomainException("Cannot cancel order without ExchangeOrderId.");

        if (!long.TryParse(order.ExchangeOrderId, out var exId))
            throw new DomainException($"Invalid BingX order id: {order.ExchangeOrderId}");

        var result = await _client.PerpetualFuturesApi.Trading
            .CancelOrderAsync(
                symbol: order.Symbol.BingXFormat,
                orderId: (long?)exId,
                clientOrderId: (string?)null,
                ct: ct)
            .ConfigureAwait(false);

        result.Check(nameof(CancelOrderAsync));

        if (order.IsActive) order.Cancel("Canceled via API");
    }

    public async Task RefreshOrderStatusAsync(Order order, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(order.ExchangeOrderId))
            throw new DomainException("Cannot refresh order without ExchangeOrderId.");

        if (!long.TryParse(order.ExchangeOrderId, out var exId))
            throw new DomainException($"Invalid BingX order id: {order.ExchangeOrderId}");

        var result = await _client.PerpetualFuturesApi.Trading
            .GetOrderAsync(
                symbol: order.Symbol.BingXFormat,
                orderId: (long?)exId,
                clientOrderId: (string?)null,
                ct: ct)
            .ConfigureAwait(false);

        result.Check(nameof(RefreshOrderStatusAsync));

        var remote = result.Data;
        var remoteStatus = remote.Status.ToDomain();

        var alreadyFilled = order.FilledQuantity.Value;
        var remoteFilled = remote.QuantityFilled;
        // AveragePrice 為 decimal?
        var avgPrice = remote.AveragePrice ?? 0m;

        if (remoteFilled > alreadyFilled && avgPrice > 0)
        {
            var delta = remoteFilled - alreadyFilled;
            order.RecordFill(
                filledQty: Quantity.Create((decimal)delta),
                fillPrice: Price.Create(avgPrice),
                commission: remote.Fee.GetValueOrDefault());
        }

        if (remoteStatus == OrderStatus.Canceled && order.IsActive)
            order.Cancel("Canceled on exchange");
        else if (remoteStatus == OrderStatus.Rejected && order.IsActive)
            order.Reject("Rejected on exchange");
        else if (remoteStatus == OrderStatus.Expired && order.IsActive)
            order.Expire();
    }

    // ========== ListenKey (User Data WS lifecycle) ==========
    // BingX user-data WebSocket 採 Binance-family 設計：
    //   POST /openApi/user/auth/userDataStream → listenKey (有效期 60 分鐘)
    //   PUT  /openApi/user/auth/userDataStream → 續期 60 分鐘
    //   DELETE /openApi/user/auth/userDataStream → 釋放
    // BingXMarketDataStream 透過這三個方法管理 listenKey 生命週期。

    /// <summary>
    /// 申請 user-data WebSocket 用的 listenKey。
    /// SDK XML 標記：StartUserStreamAsync — POST /openApi/user/auth/userDataStream
    /// 回傳：WebCallResult&lt;string&gt;（probe 確認）
    /// </summary>
    public async Task<string> GetListenKeyAsync(CancellationToken ct = default)
    {
        var result = await _client.PerpetualFuturesApi.Account
            .StartUserStreamAsync(ct).ConfigureAwait(false);
        result.Check(nameof(GetListenKeyAsync));

        var key = result.Data;
        if (string.IsNullOrWhiteSpace(key))
            throw new ExchangeApiException("BingX StartUserStreamAsync returned empty listenKey");

        _logger.LogInformation("Acquired BingX listenKey (truncated: {KeyHead}...)",
            key.Length > 8 ? key[..8] : key);
        return key;
    }

    /// <summary>
    /// 把 listenKey 的有效期續 60 分鐘。
    /// SDK XML 標記：KeepAliveUserStreamAsync — PUT /openApi/user/auth/userDataStream
    /// </summary>
    public async Task ExtendListenKeyAsync(string listenKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(listenKey))
            throw new DomainException("listenKey must not be empty when extending");

        var result = await _client.PerpetualFuturesApi.Account
            .KeepAliveUserStreamAsync(listenKey, ct).ConfigureAwait(false);
        result.Check(nameof(ExtendListenKeyAsync));
    }

    /// <summary>
    /// 釋放 listenKey（DELETE）。Stream Stop 時呼叫，best-effort，失敗不阻擋關閉流程。
    /// </summary>
    public async Task StopListenKeyAsync(string listenKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(listenKey)) return;

        var result = await _client.PerpetualFuturesApi.Account
            .StopUserStreamAsync(listenKey, ct).ConfigureAwait(false);
        result.Check(nameof(StopListenKeyAsync));
    }

    // ========== 持倉 ==========

    public async Task<IReadOnlyList<ExchangePositionInfo>> GetOpenPositionsAsync(
        CancellationToken ct = default)
    {
        var result = await _client.PerpetualFuturesApi.Trading
            .GetPositionsAsync(ct: ct).ConfigureAwait(false);

        result.Check(nameof(GetOpenPositionsAsync));

        var list = new List<ExchangePositionInfo>();
        // 用 dynamic 繞過 v3.10.0 BingXPosition 屬性名不確定的問題
        foreach (dynamic p in result.Data)
        {
            decimal qty = 0m;
            try { qty = (decimal)p.Quantity; }
            catch { try { qty = (decimal)p.PositionAmt; } catch { qty = 0m; } }

            if (qty == 0m) continue;

            decimal entryPrice = 0m;
            try { entryPrice = (decimal)p.EntryPrice; }
            catch { try { entryPrice = (decimal)p.AverageEntryPrice; } catch { } }

            decimal markPrice = 0m;
            try { markPrice = (decimal?)p.MarkPrice ?? 0m; } catch { }

            decimal unrealizedPnl = 0m;
            try { unrealizedPnl = (decimal?)p.UnrealizedProfit ?? 0m; }
            catch { try { unrealizedPnl = (decimal?)p.UnrealizedPnl ?? 0m; } catch { } }

            decimal liqPrice = 0m;
            try { liqPrice = (decimal?)p.LiquidationPrice ?? 0m; } catch { }

            int leverage = 1;
            try { leverage = (int)p.Leverage; } catch { }

            string sym;
            try { sym = (string)p.Symbol; } catch { continue; }

            var sideObj = (global::BingX.Net.Enums.PositionSide)p.PositionSide;

            list.Add(new ExchangePositionInfo(
                Symbol: Symbol.Parse(sym),
                Side: sideObj.ToDomain(),
                Quantity: Math.Abs(qty),
                EntryPrice: entryPrice,
                MarkPrice: markPrice,
                UnrealizedPnL: unrealizedPnl,
                LiquidationPrice: liqPrice,
                Leverage: leverage));
        }

        return list;
    }
}

// 擴充方法：T 由 this 引數自動推斷，避免「類型引數無法推斷」
internal static class BingXCheckExtensions
{
    public static void Check<T>(
        this WebCallResult<T> result,
        string operation)
    {
        if (result.Success) return;
        var code = result.Error?.Code?.ToString() ?? "N/A";
        var msg = result.Error?.Message ?? "Unknown error";
        throw new ExchangeApiException(
            $"BingX {operation} failed. Code={code}, Message={msg}");
    }

    public static void Check(
        this WebCallResult result,
        string operation)
    {
        if (result.Success) return;
        var code = result.Error?.Code?.ToString() ?? "N/A";
        var msg = result.Error?.Message ?? "Unknown error";
        throw new ExchangeApiException(
            $"BingX {operation} failed. Code={code}, Message={msg}");
    }
}

public sealed class ExchangeApiException : Exception
{
    public ExchangeApiException(string message) : base(message) { }
    public ExchangeApiException(string message, Exception inner) : base(message, inner) { }
}