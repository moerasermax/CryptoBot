using System.Diagnostics;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Realtime;
using CryptoBot.ConsoleApp.Realtime;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.ConsoleApp.Services;

/// <summary>
/// S27 T2：交易所連線心跳 — 每 <see cref="ProbeInterval"/> 對 REST 端點做一次
/// 極輕量 BookTicker 呼叫（已是公開端點，不需驗證），用 <see cref="Stopwatch"/>
/// 測 RTT，再透過 <see cref="DashboardEventBus"/> 推給 <c>GlobalStatusBar</c>。
///
/// <para>
/// 為什麼用 BookTicker 而不是 GetServerTime：
/// <list type="bullet">
///   <item>BingX v3.10 SDK 沒有統一的 ServerTime endpoint wrapper；</item>
///   <item>BookTicker 純公開 REST，不碰 user data，與 HealthCheck 語意一致；</item>
///   <item>量級約 200 bytes / 一次呼叫 — 比真正 ping 包只多 TLS 握手，可忽略。</item>
/// </list>
/// </para>
///
/// <para>
/// 錯誤處理：任何例外都吞掉、發布 <c>IsHealthy=false</c> 的心跳 — 背景 loop 絕不會因單次
/// 網路抖動而崩潰（UI 只要看到紅燈就懂「連不上了」）。
/// </para>
/// </summary>
public sealed class ExchangeHealthCheckService : BackgroundService
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(15);
    private static readonly Symbol ProbeSymbol = Symbol.Parse("BTC-USDT");

    private readonly IExchangeClient _exchange;
    private readonly DashboardEventBus _bus;
    private readonly ILogger<ExchangeHealthCheckService> _logger;

    public ExchangeHealthCheckService(
        IExchangeClient exchange,
        DashboardEventBus bus,
        ILogger<ExchangeHealthCheckService> logger)
    {
        _exchange = exchange;
        _bus = bus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "💓 ExchangeHealthCheckService started — probing {Exchange} every {Seconds}s.",
            _exchange.ExchangeName, ProbeInterval.TotalSeconds);

        // 啟動時先採一次 — UI 不用等 15 秒才有數字
        await ProbeOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(ProbeInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await ProbeOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProbeOnceAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int latencyMs;
        bool healthy;
        string? error = null;

        try
        {
            _ = await _exchange.GetMarkPriceAsync(ProbeSymbol, ct).ConfigureAwait(false);
            sw.Stop();
            latencyMs = (int)sw.ElapsedMilliseconds;
            healthy = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            // 失敗也把耗時記下來（多半是超時 / DNS 錯）— 對使用者判讀有用。
            latencyMs = (int)sw.ElapsedMilliseconds;
            healthy = false;
            error = ex.GetType().Name + ": " + ex.Message;
            _logger.LogWarning(ex,
                "Health probe to {Exchange} failed after {Ms}ms.", _exchange.ExchangeName, latencyMs);
        }

        _bus.RaiseExchangeHealth(new ExchangeHealthUpdate(
            Timestamp: DateTime.UtcNow,
            ExchangeName: _exchange.ExchangeName,
            LatencyMs: latencyMs,
            IsHealthy: healthy,
            ErrorMessage: error));
    }
}
