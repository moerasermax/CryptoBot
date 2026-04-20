using System.Collections.Concurrent;
using BingX.Net;
using BingX.Net.Clients;
using BingX.Net.Objects.Models;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.MarketDataAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using CryptoExchange.Net.Objects.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AppBingXOptions = CryptoBot.Infrastructure.Configuration.BingXOptions;

namespace CryptoBot.Infrastructure.Exchange.BingX;

/// <summary>
/// BingX WebSocket 行情資料流 (IMarketDataStream 實作)。
///
/// 設計要點：
/// - 每個 (Symbol, Kind) 組合對應一個 UpdateSubscription，存放在 ConcurrentDictionary
/// - 斷線重連由 JKorf CryptoExchange.Net 底層自動處理 (v3.x automatic management)；
///   本類只負責在 Unsubscribe / Stop / Dispose 時關閉
/// - 所有 SDK 方法呼叫 ct 一律具名 ct: ct (血淚教訓 #5)
/// - 所有 SDK 型別加 global:: 前綴避開命名空間衝突 (#1)
/// - Lambda handler 全部用強型別 (BingX.Net 3.10.0 XML doc + dotnet build probe 確認)
///
/// User-data WebSocket 流程 (Binance-family pattern)：
///   1. REST POST /openApi/user/auth/userDataStream 拿 listenKey
///   2. WS  SubscribeToUserDataUpdatesAsync(listenKey, ...) 開始接收
///   3. 每 30 分鐘 PUT /openApi/user/auth/userDataStream 續期 60 分鐘
///   4. 收到 ListenKeyExpired event 時記錄並失效目前 key (重啟流程處理重新申請)
///   5. Stop 時 DELETE listenKey 釋放
/// </summary>
public sealed class BingXMarketDataStream : IMarketDataStream
{
    /// <summary>
    /// listenKey 續期間隔。BingX listenKey 有效期 60 分鐘，這裡取 30 分鐘留 50% 安全邊際，
    /// 即使一次續期失敗，還有約 30 分鐘的下一次重試機會才會真正過期。
    /// </summary>
    private static readonly TimeSpan ListenKeyRenewInterval = TimeSpan.FromMinutes(30);

    private readonly BingXSocketClient _socketClient;
    private readonly BingXExchangeClient _restClient;
    private readonly AppBingXOptions _options;
    private readonly ILogger<BingXMarketDataStream> _logger;

    private readonly ConcurrentDictionary<SubscriptionKey, UpdateSubscription> _subscriptions = new();
    private readonly SemaphoreSlim _startStopLock = new(1, 1);

    private UpdateSubscription? _userDataSubscription;
    private CancellationTokenSource? _renewCts;
    private Task? _renewLoopTask;
    private string? _activeListenKey;

    private bool _started;
    private bool _disposed;
    private readonly bool _hasCredentials;

    public event Func<Symbol, KlineInterval, Kline, Task>? OnKlineUpdate;
    public event Func<Symbol, Price, Task>? OnPriceUpdate;
    public event Func<ExchangeOrderUpdate, Task>? OnExchangeOrderUpdate;
    public event Func<ExchangeAccountUpdate, Task>? OnExchangeAccountUpdate;

    public BingXMarketDataStream(
        BingXExchangeClient restClient,
        IOptions<AppBingXOptions> options,
        ILogger<BingXMarketDataStream> logger)
    {
        _restClient = restClient;
        _options = options.Value;
        _logger = logger;

        _socketClient = new BingXSocketClient(opts =>
        {
            opts.Environment = _options.UseDemoTrading
                ? global::BingX.Net.BingXEnvironment.Demo
                : global::BingX.Net.BingXEnvironment.Live;
        });

        if (!string.IsNullOrWhiteSpace(_options.ApiKey) &&
            !string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            BingXCredentials creds = new BingXCredentials()
            {
                Key = _options.ApiKey,
                Secret = _options.ApiSecret,
            };
            _socketClient.PerpetualFuturesApi.SetApiCredentials(creds);
            _socketClient.SpotApi.SetApiCredentials(creds);
            _hasCredentials = true;
        }
    }

    // ========== 生命週期 ==========

    public async Task StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _startStopLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_started) return;

            if (_hasCredentials)
            {
                await OpenUserStreamAsync(ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation(
                    "BingX stream starting in market-data-only mode (no API credentials provided)");
            }

            _started = true;
            _logger.LogInformation("BingX market data stream started (Mode={Mode})",
                _options.UseDemoTrading ? "DEMO" : "LIVE");
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        await _startStopLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_started) return;

            // 先停掉續期循環避免在 listenKey 釋放期間還呼叫續期 API
            await StopRenewLoopAsync().ConfigureAwait(false);

            foreach (var kv in _subscriptions)
            {
                try { await kv.Value.CloseAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error closing subscription {Key}", kv.Key); }
            }
            _subscriptions.Clear();

            if (_userDataSubscription is not null)
            {
                try { await _userDataSubscription.CloseAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error closing user data subscription"); }
                _userDataSubscription = null;
            }

            // Best-effort 釋放 listenKey
            if (!string.IsNullOrEmpty(_activeListenKey))
            {
                try
                {
                    await _restClient.StopListenKeyAsync(_activeListenKey, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "StopListenKey failed; ignoring (key will expire naturally)");
                }
                _activeListenKey = null;
            }

            _started = false;
            _logger.LogInformation("BingX market data stream stopped");
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    // ========== 公開訂閱方法（行情）==========

    public async Task SubscribeKlinesAsync(
        Symbol symbol, KlineInterval interval, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var key = SubscriptionKey.Kline(symbol, interval);
        if (_subscriptions.ContainsKey(key))
        {
            _logger.LogDebug("Already subscribed: {Key}", key);
            return;
        }

        var bxInterval = interval.ToBingX();

        // SDK 簽章 (v3.10.0 XML 確認):
        //   SubscribeToKlineUpdatesAsync(string symbol, KlineInterval interval,
        //       Action<DataEvent<BingXFuturesKlineUpdate[]>> onMessage,
        //       CancellationToken ct = default)
        // 注意 handler 拿到的是 *陣列*，每個 tick 通常是 1 筆「進行中」K 線
        var result = await _socketClient.PerpetualFuturesApi
            .SubscribeToKlineUpdatesAsync(
                symbol.BingXFormat,
                bxInterval,
                evt => _ = HandleKlineUpdate(symbol, interval, evt),
                ct: ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            var msg = result.Error?.Message ?? "unknown";
            throw new ExchangeApiException(
                $"BingX SubscribeKlines failed for {symbol.BingXFormat} {interval}: {msg}");
        }

        _subscriptions[key] = result.Data;
        HookSubscriptionLifecycle(result.Data, key);
        _logger.LogInformation("Subscribed Klines {Symbol} {Interval}",
            symbol.BingXFormat, interval);
    }

    public async Task SubscribeMarkPriceAsync(Symbol symbol, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var key = SubscriptionKey.Price(symbol);
        if (_subscriptions.ContainsKey(key))
        {
            _logger.LogDebug("Already subscribed: {Key}", key);
            return;
        }

        // SDK 3.10.0 已提供原生 MarkPrice WS 訂閱（HANDOFF_3 寫的「沒有 MarkPrice topic」是錯的）
        // SubscribeToMarkPriceUpdatesAsync(string symbol,
        //     Action<DataEvent<BingXMarkPriceUpdate>> onMessage,
        //     CancellationToken ct = default)
        var result = await _socketClient.PerpetualFuturesApi
            .SubscribeToMarkPriceUpdatesAsync(
                symbol.BingXFormat,
                evt => _ = HandleMarkPriceUpdate(symbol, evt),
                ct: ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            var msg = result.Error?.Message ?? "unknown";
            throw new ExchangeApiException(
                $"BingX SubscribeMarkPrice failed for {symbol.BingXFormat}: {msg}");
        }

        _subscriptions[key] = result.Data;
        HookSubscriptionLifecycle(result.Data, key);
        _logger.LogInformation("Subscribed MarkPrice {Symbol}", symbol.BingXFormat);
    }

    public async Task UnsubscribeAsync(Symbol symbol, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var keysToRemove = _subscriptions.Keys
            .Where(k => k.Symbol.Equals(symbol))
            .ToList();

        foreach (var key in keysToRemove)
        {
            if (_subscriptions.TryRemove(key, out var sub))
            {
                try
                {
                    await sub.CloseAsync().ConfigureAwait(false);
                    _logger.LogInformation("Unsubscribed {Key}", key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error unsubscribing {Key}", key);
                }
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    // ========== Strong-typed payload handlers ==========

    private async Task HandleKlineUpdate(
        Symbol symbol,
        KlineInterval interval,
        DataEvent<BingXFuturesKlineUpdate[]> evt)
    {
        var handler = OnKlineUpdate;
        if (handler is null) return;

        var batch = evt.Data;
        if (batch is null || batch.Length == 0) return;

        // BingX 永續合約 K 線 push: 每 tick 通常一根「進行中」K 線；
        // 若 SDK 在某些情境一次推多根，我們仍以最末筆當最新狀態。
        var k = batch[^1];

        if (k.OpenPrice <= 0 || k.HighPrice <= 0 || k.LowPrice <= 0 || k.ClosePrice <= 0)
            return;

        var openTime = k.Timestamp;
        var closeTime = openTime + IntervalSpan(interval);

        Kline kline;
        try
        {
            kline = Kline.Create(
                openTime: openTime,
                closeTime: closeTime,
                open: k.OpenPrice,
                high: k.HighPrice,
                low: k.LowPrice,
                close: k.ClosePrice,
                volume: k.Volume < 0 ? 0m : k.Volume,
                interval: interval);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Kline.Create rejected payload for {Symbol} {Interval}; dropping tick",
                symbol.BingXFormat, interval);
            return;
        }

        try { await handler(symbol, interval, kline).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnKlineUpdate handler threw for {Symbol} {Interval}",
                symbol.BingXFormat, interval);
        }
    }

    private async Task HandleMarkPriceUpdate(Symbol symbol, DataEvent<BingXMarkPriceUpdate> evt)
    {
        var handler = OnPriceUpdate;
        if (handler is null) return;

        var mp = evt.Data;
        if (mp is null || mp.MarkPrice <= 0) return;

        Price price;
        try { price = Price.Create(mp.MarkPrice); }
        catch { return; }

        try { await handler(symbol, price).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnPriceUpdate handler threw for {Symbol}", symbol.BingXFormat);
        }
    }

    // ========== User-data WebSocket ==========

    private async Task OpenUserStreamAsync(CancellationToken ct)
    {
        try
        {
            _activeListenKey = await _restClient.GetListenKeyAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to obtain listenKey; user-data WS disabled (orders will rely on REST polling)");
            return;
        }

        try
        {
            // SDK 簽章 (v3.10.0 XML 確認):
            //   SubscribeToUserDataUpdatesAsync(
            //       string listenKey,
            //       Action<DataEvent<BingXFuturesAccountUpdate>> onAccountUpdate,
            //       Action<DataEvent<BingXFuturesOrderUpdate>>   onOrderUpdate,
            //       Action<DataEvent<BingXConfigUpdate>>         onConfigurationUpdate,
            //       Action<DataEvent<BingXListenKeyExpiredUpdate>> onListenKeyExpiredUpdate,
            //       CancellationToken ct = default)
            // 參數名 onConfigurationUpdate（不是 onConfigUpdate，HANDOFF_3 寫錯）
            var result = await _socketClient.PerpetualFuturesApi
                .SubscribeToUserDataUpdatesAsync(
                    listenKey: _activeListenKey!,
                    onAccountUpdate: evt => _ = HandleAccountUpdate(evt),
                    onOrderUpdate: evt => _ = HandleOrderUpdate(evt),
                    onConfigurationUpdate: evt => _ = HandleConfigUpdate(evt),
                    onListenKeyExpiredUpdate: evt => _ = HandleListenKeyExpired(evt),
                    ct: ct)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                _logger.LogError(
                    "BingX SubscribeToUserDataUpdatesAsync failed: {Err}. Falling back to REST polling.",
                    result.Error?.Message ?? "unknown");
                _activeListenKey = null;
                return;
            }

            _userDataSubscription = result.Data;
            _userDataSubscription.ConnectionLost += () =>
                _logger.LogWarning("BingX user-data WS: connection lost");
            _userDataSubscription.ConnectionRestored += ts =>
                _logger.LogInformation(
                    "BingX user-data WS: reconnected after {Seconds:F1}s", ts.TotalSeconds);

            // 啟動 listenKey 自動續期循環
            _renewCts = new CancellationTokenSource();
            _renewLoopTask = RenewListenKeyLoopAsync(_renewCts.Token);

            _logger.LogInformation(
                "Subscribed BingX user-data stream (listenKey acquired, auto-renew every {Min}m)",
                ListenKeyRenewInterval.TotalMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to subscribe BingX user-data stream; falling back to REST polling for order state");
            _activeListenKey = null;
        }
    }

    private async Task RenewListenKeyLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ListenKeyRenewInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                var key = _activeListenKey;
                if (string.IsNullOrEmpty(key))
                {
                    // listenKey 已被失效（可能 ListenKeyExpired event 觸發）— 中止循環
                    _logger.LogInformation("ListenKey no longer active; stopping renewal loop");
                    break;
                }

                try
                {
                    await _restClient.ExtendListenKeyAsync(key, ct).ConfigureAwait(false);
                    _logger.LogDebug("ListenKey extended (next renew in {Min}m)",
                        ListenKeyRenewInterval.TotalMinutes);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // 單次失敗不終止循環；下個間隔還能再試。
                    // 真的失效時 SDK 會送 ListenKeyExpiredUpdate 事件。
                    _logger.LogWarning(ex,
                        "ListenKey renewal failed; will retry on next interval");
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "ListenKey renewal loop terminated unexpectedly");
        }
    }

    private async Task StopRenewLoopAsync()
    {
        if (_renewCts is null) return;

        try { _renewCts.Cancel(); }
        catch { /* ignore */ }

        if (_renewLoopTask is not null)
        {
            try { await _renewLoopTask.ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Renew loop task ended with exception (expected during shutdown)");
            }
        }

        _renewCts.Dispose();
        _renewCts = null;
        _renewLoopTask = null;
    }

    // ---- User-data event handlers (strong-typed) ----

    private async Task HandleOrderUpdate(DataEvent<BingXFuturesOrderUpdate> evt)
    {
        var handler = OnExchangeOrderUpdate;
        if (handler is null) return;

        var u = evt.Data;
        if (u is null) return;

        Symbol symbol;
        try { symbol = Symbol.Parse(u.Symbol); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BingX order update: unparseable symbol {Symbol}", u.Symbol);
            return;
        }

        var dto = new ExchangeOrderUpdate(
            ExchangeOrderId: u.OrderId.ToString(),
            Symbol: symbol,
            Status: u.Status.ToDomain(),
            Quantity: u.Quantity.GetValueOrDefault(),
            QuantityFilled: u.QuantityFilled.GetValueOrDefault(),
            AverageFillPrice: u.AveragePrice,
            Fee: u.Fee.GetValueOrDefault(),
            UpdateTime: u.UpdateTime ?? DateTime.UtcNow);

        try { await handler(dto).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnExchangeOrderUpdate handler threw for order {OrderId}", u.OrderId);
        }
    }

    private async Task HandleAccountUpdate(DataEvent<BingXFuturesAccountUpdate> evt)
    {
        var handler = OnExchangeAccountUpdate;
        if (handler is null) return;

        var update = evt.Data?.Update;
        if (update is null) return;

        var balances = new List<ExchangeBalanceEntry>();
        if (update.Balances is not null)
        {
            foreach (var b in update.Balances)
            {
                balances.Add(new ExchangeBalanceEntry(
                    Asset: b.Asset ?? string.Empty,
                    Balance: b.Balance,
                    UnrealizedProfit: 0m));
            }
        }

        var positions = new List<ExchangePositionInfo>();
        if (update.Positions is not null)
        {
            foreach (var p in update.Positions)
            {
                Symbol sym;
                try { sym = Symbol.Parse(p.Symbol); }
                catch { continue; }

                var side = p.Side == global::BingX.Net.Enums.TradeSide.Long
                    ? PositionSide.Long
                    : PositionSide.Short;

                positions.Add(new ExchangePositionInfo(
                    Symbol: sym,
                    Side: side,
                    Quantity: Math.Abs(p.Size),
                    EntryPrice: p.EntryPrice,
                    MarkPrice: 0m,
                    UnrealizedPnL: p.UnrealizedPnl,
                    LiquidationPrice: 0m,
                    Leverage: 1));
            }
        }

        var dto = new ExchangeAccountUpdate(
            UpdateTime: evt.ReceiveTime,
            Balances: balances,
            Positions: positions);

        try { await handler(dto).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnExchangeAccountUpdate handler threw (balances={B} positions={P})",
                balances.Count, positions.Count);
        }
    }

    private Task HandleConfigUpdate(DataEvent<BingXConfigUpdate> evt)
    {
        // Leverage / marginMode 變更事件 — 只記 log，不轉往上層
        _logger.LogInformation("BingX config update: {Payload}",
            evt.Data?.Configuration?.ToString() ?? "<null>");
        return Task.CompletedTask;
    }

    private Task HandleListenKeyExpired(DataEvent<BingXListenKeyExpiredUpdate> evt)
    {
        _logger.LogWarning(
            "BingX ListenKey expired event received: {Key}. " +
            "Marking key invalid; user-data updates may be missed until stream is restarted.",
            evt.Data?.ListenKey ?? "<null>");

        // 立即失效目前 key — 續期循環下個 tick 會偵測到並退出。
        // 立即重新申請 + 重訂閱會與 SDK 自動重連產生 race，
        // 留給上層 (Hosted Service / restart logic) 處理。
        _activeListenKey = null;
        return Task.CompletedTask;
    }

    // ========== Helpers ==========

    private static TimeSpan IntervalSpan(KlineInterval interval) => interval.ToTimeSpan();

    private void HookSubscriptionLifecycle(UpdateSubscription sub, SubscriptionKey key)
    {
        sub.ConnectionLost += () =>
            _logger.LogWarning("BingX WS connection lost: {Key} (auto-reconnect by SDK)", key);

        sub.ConnectionRestored += ts =>
            _logger.LogInformation("BingX WS reconnected: {Key} after {Seconds:F1}s",
                key, ts.TotalSeconds);

        sub.Exception += ex =>
            _logger.LogError(ex, "BingX WS exception on {Key}", key);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BingXMarketDataStream));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during BingXMarketDataStream.DisposeAsync → StopAsync");
        }

        try { _socketClient.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error disposing BingXSocketClient"); }

        _startStopLock.Dispose();
        _disposed = true;
    }

    // ========== 訂閱鍵（內部值物件）==========

    private readonly record struct SubscriptionKey(
        Symbol Symbol,
        SubscriptionKind Kind,
        KlineInterval? Interval)
    {
        public static SubscriptionKey Kline(Symbol s, KlineInterval i) =>
            new(s, SubscriptionKind.Kline, i);

        public static SubscriptionKey Price(Symbol s) =>
            new(s, SubscriptionKind.MarkPrice, null);

        public override string ToString() => Kind switch
        {
            SubscriptionKind.Kline => $"Kline({Symbol.BingXFormat},{Interval})",
            SubscriptionKind.MarkPrice => $"MarkPrice({Symbol.BingXFormat})",
            _ => $"Unknown({Symbol.BingXFormat})",
        };
    }

    private enum SubscriptionKind
    {
        Kline,
        MarkPrice,
    }
}
