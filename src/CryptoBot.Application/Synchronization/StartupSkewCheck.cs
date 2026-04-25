using CryptoBot.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace CryptoBot.Application.Synchronization;

/// <summary>
/// S66-E：<see cref="IStartupHealthCheck"/> 的實作 —— 純資料層。
///
/// 透過 <see cref="ISkewMeasurementService"/> 取得單次包夾測量結果，
/// 依閾值（與 <see cref="NtpDriftMonitor"/> 一致：500ms warning / 1000ms unsafe）判定狀態，
/// 回傳 <see cref="StartupCheckResult"/> 給 ConsoleApp 渲染 banner。
///
/// **嚴格 IRON ⑥**：本類別**不**呼叫任何 <c>Console.*</c>、不寫 ASCII art、不決定 exit code。
/// 那些是 ConsoleApp 層 <c>StartupBannerRenderer</c> + <c>Program.cs</c> 的職責。
/// </summary>
public sealed class StartupSkewCheck : IStartupHealthCheck
{
    /// <summary>偏差 ≤ 500ms 視為 Safe（與 NtpDriftMonitor.WarningThreshold 一致）</summary>
    internal const double WarningThresholdMs = 500d;

    /// <summary>偏差 > 1000ms 視為 Unsafe（與 RiskManager.ClockSkewRejectThresholdMs 一致）</summary>
    internal const double UnsafeThresholdMs = 1000d;

    private readonly ISkewMeasurementService _measurement;
    private readonly IExchangeClient _exchange;
    private readonly ILogger<StartupSkewCheck> _logger;

    public StartupSkewCheck(
        ISkewMeasurementService measurement,
        IExchangeClient exchange,
        ILogger<StartupSkewCheck> logger)
    {
        _measurement = measurement;
        _exchange = exchange;
        _logger = logger;
    }

    public async Task<StartupCheckResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            var m = await _measurement.MeasureAsync(ct).ConfigureAwait(false);
            var skewMs = (long)m.Offset.TotalMilliseconds;
            var roundTripMs = (long)m.RoundTrip.TotalMilliseconds;
            var absMs = Math.Abs(skewMs);

            var status = absMs > UnsafeThresholdMs
                ? SkewStatus.Unsafe
                : absMs > WarningThresholdMs
                    ? SkewStatus.Warning
                    : SkewStatus.Safe;

            var advice = status switch
            {
                SkewStatus.Unsafe => "管理員 PowerShell 跑 `w32tm /resync /force` 後重啟 ConsoleApp。" +
                                     "RiskManager 會擋住所有真實下單訊號（NTP Drift detected）。",
                SkewStatus.Warning => "建議盡快跑 `w32tm /resync` 校時，距離 RiskManager 1000ms 攔截線僅 " +
                                     $"{UnsafeThresholdMs - absMs:F0}ms 邊界。",
                _ => null,
            };

            return new StartupCheckResult(
                ExchangeName: _exchange.ExchangeName,
                Mode: _exchange.CurrentMode,
                SkewStatus: status,
                OffsetMs: skewMs,
                RoundTripMs: roundTripMs,
                ActionAdvice: advice,
                ErrorMessage: null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup skew check measurement failed.");
            return new StartupCheckResult(
                ExchangeName: _exchange.ExchangeName,
                Mode: _exchange.CurrentMode,
                SkewStatus: SkewStatus.MeasurementFailed,
                OffsetMs: null,
                RoundTripMs: null,
                ActionAdvice: "無法量測時鐘偏差（網路 / API / 金鑰問題）。建議跑 `dotnet run -- environment` 確認 API 連線。",
                ErrorMessage: $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
