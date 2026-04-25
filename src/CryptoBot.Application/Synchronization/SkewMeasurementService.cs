using CryptoBot.Application.Common.Interfaces;

namespace CryptoBot.Application.Synchronization;

/// <summary>
/// S66-E：<see cref="ISkewMeasurementService"/> 的標準實作。
/// </summary>
public sealed class SkewMeasurementService : ISkewMeasurementService
{
    private readonly IExchangeClient _exchange;
    private readonly TimeProvider _clock;

    public SkewMeasurementService(IExchangeClient exchange, TimeProvider? clock = null)
    {
        _exchange = exchange;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<SkewMeasurement> MeasureAsync(CancellationToken ct = default)
    {
        var localBefore = _clock.GetUtcNow().UtcDateTime;
        var serverTime = await _exchange.GetServerTimeAsync(ct).ConfigureAwait(false);
        var localAfter = _clock.GetUtcNow().UtcDateTime;

        // 中點計算採 Ticks 加總平均，避開 DateTime 加法 overflow / 精度損失
        var midTicks = localBefore.Ticks + (localAfter.Ticks - localBefore.Ticks) / 2;
        var localMid = new DateTime(midTicks, DateTimeKind.Utc);

        return new SkewMeasurement(
            LocalBeforeUtc: localBefore,
            ServerTimeUtc: serverTime,
            LocalAfterUtc: localAfter,
            LocalMidUtc: localMid,
            Offset: serverTime - localMid,
            RoundTrip: localAfter - localBefore);
    }
}
