using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Synchronization;

/// <summary>
/// S72：帳戶對帳的週期性背景服務 — 補完 <see cref="IAccountSynchronizer.ReconcileAsync"/>
/// 原本「只在啟動時跑一次」的紀律盲區（IM §S72）。
///
/// <para>
/// <b>分工</b>：
/// <list type="bullet">
///   <item><see cref="OrderReconciliationService"/> — 每分鐘巡檢 Active Order 狀態（New / PartiallyFilled）</item>
///   <item><b>本服務</b> — 每 <see cref="TickInterval"/> 跑一次 <c>ReconcileAsync</c>，做「Order 狀態刷新 + Position 對帳（含 S72 實證流程）」</item>
///   <item><see cref="StrategyRuntimeHostedService"/> — 啟動時跑一次 <c>ReconcileAsync</c></item>
/// </list>
/// </para>
///
/// <para>
/// <b>為何不直接讓 OrderReconciliationService 兼差</b>：兩者頻率與職責不同 —
/// OrderReconciliationService 著重在「未取得 ExchangeOrderId 的孤兒 Order」與「殭屍 New / PartiallyFilled」，
/// 為避免 BingX REST 限流走 1 min。Position 實證對帳每筆需呼叫 GetTradeHistoryAsync（成本較高），
/// 走 5 min 較合適、且職責邊界清晰便於日後調整。
/// </para>
///
/// <para>
/// <b>IRON ⑤ 透明化</b>：每次 tick 都印 log；任何例外只 LogError 不停服務（與其他對帳服務一致）。
/// </para>
/// </summary>
public sealed class PeriodicAccountReconciliationService : BackgroundService
{
    /// <summary>巡檢頻率。Position 對帳成本高於 Order 對帳，走 5 min。</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    private readonly IAccountSynchronizer _synchronizer;
    private readonly ILogger<PeriodicAccountReconciliationService> _logger;

    public PeriodicAccountReconciliationService(
        IAccountSynchronizer synchronizer,
        ILogger<PeriodicAccountReconciliationService> logger)
    {
        _synchronizer = synchronizer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[RECONCILIATION] PeriodicAccountReconciliationService started — tick every {Min} min.",
            TickInterval.TotalMinutes);

        // 啟動刻意不立即 tick — StrategyRuntimeHostedService 啟動已跑過一次 ReconcileAsync，
        // 等一個週期讓 WS handler 與市場資料訂閱完成後再開始週期性對帳。
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _synchronizer.ReconcileAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // 預期 shutdown 路徑
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[RECONCILIATION] PeriodicAccountReconciliationService tick failed — will retry next interval.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 預期 shutdown
        }

        _logger.LogInformation("[RECONCILIATION] PeriodicAccountReconciliationService stopping.");
    }
}
