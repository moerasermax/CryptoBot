using System.Collections.Concurrent;

namespace CryptoBot.Application.RiskManagement;

/// <summary>
/// 追蹤每個策略最近一次送單時刻，供 RiskManager 判斷是否處於冷卻期。
///
/// 設計要點：
/// - 進程生命週期內 in-memory 即可（冷卻時間 ~ 秒級，重啟後歸零是可接受行為）
/// - 必須 Singleton — 跨 Scoped <see cref="RiskManager"/> 共享同一張表
/// - 時間來源可注入，方便測試（<see cref="TimeProvider"/> 預設為 <see cref="TimeProvider.System"/>）
/// </summary>
public interface IStrategyCooldownTracker
{
    /// <summary>策略目前是否在冷卻期內（true = 拒絕下單）。</summary>
    bool IsInCooldown(Guid strategyId, TimeSpan cooldown);

    /// <summary>記錄策略剛送出一筆訂單，冷卻計時由此刻起算。</summary>
    void RecordOrderPlaced(Guid strategyId);
}

public sealed class StrategyCooldownTracker : IStrategyCooldownTracker
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastOrderAt = new();
    private readonly TimeProvider _clock;

    public StrategyCooldownTracker(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    public bool IsInCooldown(Guid strategyId, TimeSpan cooldown)
    {
        if (cooldown <= TimeSpan.Zero) return false;
        if (!_lastOrderAt.TryGetValue(strategyId, out var last)) return false;
        return _clock.GetUtcNow() - last < cooldown;
    }

    public void RecordOrderPlaced(Guid strategyId)
    {
        _lastOrderAt[strategyId] = _clock.GetUtcNow();
    }
}
