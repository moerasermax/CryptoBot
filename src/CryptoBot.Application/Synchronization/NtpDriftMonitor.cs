using CryptoBot.Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Synchronization;

/// <summary>
/// S66-D：本地時鐘 vs BingX 伺服器時鐘漂移監控背景服務。
///
/// **行為**：
///   1. 啟動時**立即**同步一次（不等 5 分鐘），讓 RiskManager 一開機就有 baseline
///   2. 之後每 5 分鐘 tick 一次
///   3. 計算 <c>Offset = ServerTime − LocalTime</c>，寫入 <see cref="IClockSkewState"/>
///   4. 偏差 &gt; 500ms 時打 Warning log
///   5. 偏差 &gt; 1000ms 時不在這層攔截，僅記錄狀態 — 攔截由 <c>RiskManager</c> 在下單前判斷
///
/// **失敗處理**：API 呼叫失敗只 log + 跳過該次，**不更新 state**。
/// 既有偏差快照保留至下次成功 sync。BackgroundService 絕不因單次 API 抖動 crash。
/// </summary>
public sealed class NtpDriftMonitor : BackgroundService
{
    /// <summary>巡檢頻率。</summary>
    internal static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(5);

    /// <summary>偏差超過此值即打 Warning log（但不攔截，攔截由 RiskManager 1000ms 條款處理）。</summary>
    internal static readonly TimeSpan WarningThreshold = TimeSpan.FromMilliseconds(500);

    private readonly ISkewMeasurementService _measurement;
    private readonly IClockSkewState _skewState;
    private readonly ILogger<NtpDriftMonitor> _logger;

    public NtpDriftMonitor(
        ISkewMeasurementService measurement,
        IClockSkewState skewState,
        ILogger<NtpDriftMonitor> logger)
    {
        _measurement = measurement;
        _skewState = skewState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "NtpDriftMonitor started — initial sync now, then every {Min} min.",
            SyncInterval.TotalMinutes);

        // 啟動時立即同步一次
        await SyncOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(SyncInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SyncOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 預期的 shutdown 路徑
        }

        _logger.LogInformation("NtpDriftMonitor stopping.");
    }

    /// <summary>單次同步邏輯。<c>internal</c> 暴露給單元測試與 DiagnosticTool 直接呼叫。</summary>
    internal async Task SyncOnceAsync(CancellationToken ct)
    {
        SkewMeasurement m;
        try
        {
            m = await _measurement.MeasureAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "NTP sync failed — keeping previous skew state (last synced: {Last}).",
                _skewState.LastSyncedAtUtc?.ToString("u") ?? "(never)");
            return;
        }

        _skewState.Update(m.Offset, m.LocalAfterUtc);

        var skewMs = (long)m.Offset.TotalMilliseconds;
        var absMs = Math.Abs(skewMs);

        if (absMs > WarningThreshold.TotalMilliseconds)
        {
            _logger.LogWarning(
                "NTP sync check complete: skew is {Ms}ms — exceeds warning threshold {Threshold}ms; " +
                "RiskManager will block orders if it grows past 1000ms.",
                skewMs, (long)WarningThreshold.TotalMilliseconds);
        }
        else
        {
            _logger.LogInformation("NTP sync check complete: skew is {Ms}ms.", skewMs);
        }
    }
}
