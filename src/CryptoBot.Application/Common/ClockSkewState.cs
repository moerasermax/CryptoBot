namespace CryptoBot.Application.Common;

/// <summary>
/// S66-D：<see cref="IClockSkewState"/> 的記憶體版本。
/// 用 <c>lock</c> 守護兩個欄位的原子寫入；讀取靠 <c>volatile</c> 觀察最新值即可。
/// </summary>
public sealed class ClockSkewState : IClockSkewState
{
    private readonly object _lock = new();
    private TimeSpan _offset = TimeSpan.Zero;
    private DateTime? _lastSyncedAtUtc;

    public TimeSpan CurrentOffset
    {
        get { lock (_lock) return _offset; }
    }

    public DateTime? LastSyncedAtUtc
    {
        get { lock (_lock) return _lastSyncedAtUtc; }
    }

    public bool IsSynced
    {
        get { lock (_lock) return _lastSyncedAtUtc.HasValue; }
    }

    public void Update(TimeSpan offset, DateTime syncedAtUtc)
    {
        lock (_lock)
        {
            _offset = offset;
            _lastSyncedAtUtc = syncedAtUtc;
        }
    }
}
