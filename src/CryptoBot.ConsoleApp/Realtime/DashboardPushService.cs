using CryptoBot.Application.Realtime;
using CryptoBot.ConsoleApp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoBot.ConsoleApp.Realtime;

/// <summary>
/// 每 <see cref="PushInterval"/> 把儀表板三卡透過 SignalR 廣播一次 —
/// 讓 UI 即使沒發生交易也有「心跳」感，餘額 / 未實現 PnL / 活躍策略數會隨時間變化。
///
/// 每輪都開一個獨立的 DI scope，確保 <see cref="DashboardStatsService"/> 拿到乾淨的 DbContext。
/// 例外一律吞掉（只 log），不讓背景 loop 因為一次拉資料失敗而整個掛掉。
/// </summary>
public sealed class DashboardPushService : BackgroundService
{
    private static readonly TimeSpan PushInterval = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRealtimeBroadcaster _broadcaster;
    private readonly ILogger<DashboardPushService> _logger;

    public DashboardPushService(
        IServiceScopeFactory scopeFactory,
        IRealtimeBroadcaster broadcaster,
        ILogger<DashboardPushService> logger)
    {
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("📡 DashboardPushService started — pushing stats every {Seconds}s.", PushInterval.TotalSeconds);

        using var timer = new PeriodicTimer(PushInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var stats = scope.ServiceProvider.GetRequiredService<DashboardStatsService>();
                var update = await stats.GetAsync(stoppingToken).ConfigureAwait(false);
                await _broadcaster.BroadcastStatsAsync(update, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DashboardPushService tick failed — will retry next interval.");
            }
        }
    }
}
