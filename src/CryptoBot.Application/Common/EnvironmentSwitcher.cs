using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Strategies;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Common;

/// <summary>
/// <see cref="IEnvironmentSwitcher"/> 的唯一實作 — Singleton 由 DI 管。
///
/// 流程（依序）：
/// <list type="number">
///   <item>取 <see cref="_switchLock"/> 序列化，確保同一時間只跑一次切換。</item>
///   <item>沒變 → 只廣播 echo 事件後 return（不做 IO）。</item>
///   <item>呼叫 <see cref="IStrategyRuntimeController.StopAllAsync"/> 把 executor 全停 —
///         從這刻起就不會再有任何 WS / REST 用舊 endpoint 觸發。</item>
///   <item><see cref="IExchangeClient.ReconfigureAsync"/> 換 REST client。</item>
///   <item><see cref="IMarketDataStream.ReconfigureAsync"/> 換 socket client（内部會先 Stop 再 Build）。</item>
///   <item>重啟 market data stream（讓 listenKey / 基本訂閱 back up）。</item>
///   <item>廣播 <see cref="IEnvironmentSwitcher.EnvironmentChanged"/>，訂閱者（UI、通知）拿到資訊。</item>
/// </list>
///
/// 為什麼不自動 re-start 被停掉的策略？
/// — 跨環境再起會是「老闆沒按的情況下，demo 配置可能跑到 live」風險太高，
/// 強制人為點一次 Start 做二次確認。切完後狀態是「停止、等你去新環境重掛」。
/// </summary>
public sealed class EnvironmentSwitcher : IEnvironmentSwitcher
{
    private readonly IExchangeClient _exchange;
    private readonly IMarketDataStream _marketData;
    private readonly IStrategyRuntimeController _runtime;
    private readonly ILogger<EnvironmentSwitcher> _logger;
    private readonly SemaphoreSlim _switchLock = new(1, 1);

    public EnvironmentSwitcher(
        IExchangeClient exchange,
        IMarketDataStream marketData,
        IStrategyRuntimeController runtime,
        ILogger<EnvironmentSwitcher> logger)
    {
        _exchange = exchange;
        _marketData = marketData;
        _runtime = runtime;
        _logger = logger;
    }

    public TradingMode CurrentMode => _exchange.CurrentMode;

    public event Action<EnvironmentChangedEvent>? EnvironmentChanged;

    public async Task<EnvironmentSwitchResult> SwitchAsync(
        TradingMode newMode, string? reason = null, CancellationToken ct = default)
    {
        await _switchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fromMode = _exchange.CurrentMode;
            if (fromMode == newMode)
            {
                _logger.LogInformation("EnvironmentSwitcher: already in {Mode} — no-op (will still broadcast).",
                    newMode);
                var echo = new EnvironmentChangedEvent(fromMode, newMode, Array.Empty<Guid>(), DateTime.UtcNow, reason);
                RaiseChanged(echo);
                return new EnvironmentSwitchResult(fromMode, newMode, Array.Empty<Guid>(), echo.ChangedAtUtc, reason);
            }

            _logger.LogWarning("⚡ EnvironmentSwitcher: switching {From} → {To}. Reason={Reason}",
                fromMode, newMode, reason ?? "<none>");

            // 1) Stop-First：把所有 executor 停掉，帶上切換原因好讓 DB audit 留痕
            var stopReason = $"Environment switched to {newMode}" + (string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason})");
            var stopped = await _runtime.StopAllAsync(stopReason, ct).ConfigureAwait(false);

            // 2) REST client 換新 endpoint
            await _exchange.ReconfigureAsync(newMode, ct).ConfigureAwait(false);

            // 3) MarketData 收 WS → 重建 socket client（內部自己 stop）
            await _marketData.ReconfigureAsync(newMode, ct).ConfigureAwait(false);

            // 4) 重啟 market data stream — 行情 + user-data listenKey 重新接
            try { await _marketData.StartAsync(ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Market data stream failed to start after switch to {Mode} — stream will remain stopped.", newMode);
            }

            var evt = new EnvironmentChangedEvent(fromMode, newMode, stopped, DateTime.UtcNow, reason);
            RaiseChanged(evt);

            _logger.LogWarning(
                "✅ Environment switched {From} → {To} | stopped {Count} strategies (they need manual restart in new env)",
                fromMode, newMode, stopped.Count);

            return new EnvironmentSwitchResult(fromMode, newMode, stopped, evt.ChangedAtUtc, reason);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    private void RaiseChanged(EnvironmentChangedEvent evt)
    {
        var handler = EnvironmentChanged;
        if (handler is null) return;

        // 單一 handler 拋例外不能影響其他訂閱者 — 逐個呼叫
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<EnvironmentChangedEvent>)d)(evt); }
            catch (Exception ex) { _logger.LogError(ex, "EnvironmentChanged subscriber threw — continuing."); }
        }
    }
}
