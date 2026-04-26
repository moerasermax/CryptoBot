namespace CryptoBot.Application.RiskManagement;

/// <summary>
/// 當日虧損熔斷 (S28 T1) 的狀態容器。
///
/// 契約：
/// - <see cref="IsTripped"/> 為 true 表示當前處於熔斷狀態；getter 為純讀取，不會觸發副作用。
/// - <see cref="Trip"/> 冪等：已在熔斷狀態重複呼叫不會覆蓋原因與時間，回 false 表示狀態未變。
/// - <see cref="Reset"/> 為「顯式解除」入口 — 管理員手動解除與跨日自動解除都透過此方法，
///   以統一事件路徑；差別只在 adminNote 內容。
/// - <see cref="SafetyBreakerMonitor"/> 會在每次 tick 檢查 <see cref="TrippedAtUtc"/> 的日期
///   是否早於今天，若是則主動呼叫 <see cref="Reset"/>(auto-reset) — 這讓跨日後 UI 透過事件
///   推播拿到解鎖通知，而不必倚賴「reader 自動解鎖」這種會製造靜默事件的 anti-pattern。
///
/// 執行緒安全：單一 <c>lock</c> 串行化所有狀態改動與讀取。
/// </summary>
public interface ISafetyBreakerState
{
    bool IsTripped { get; }
    DateTime? TrippedAtUtc { get; }
    string? Reason { get; }

    /// <summary>嘗試將熔斷標記設為 tripped；已在熔斷中回 false（冪等）。</summary>
    bool Trip(string reason);

    /// <summary>解除熔斷；未在熔斷中回 false。adminNote 會透過事件傳出，供 log / UI 顯示。</summary>
    bool Reset(string adminNote);

    /// <summary>狀態首次從 not-tripped 轉入 tripped 時觸發一次（reason）。</summary>
    event Action<string>? Tripped;

    /// <summary>狀態從 tripped 轉回 not-tripped 時觸發一次（adminNote）。</summary>
    event Action<string>? ResetBySupervisor;
}

public sealed class SafetyBreakerState : ISafetyBreakerState
{
    private readonly object _gate = new();
    private DateTime? _trippedAtUtc;
    private string? _reason;

    public event Action<string>? Tripped;
    public event Action<string>? ResetBySupervisor;

    public bool IsTripped
    {
        get { lock (_gate) return _trippedAtUtc is not null; }
    }

    public DateTime? TrippedAtUtc
    {
        get { lock (_gate) return _trippedAtUtc; }
    }

    public string? Reason
    {
        get { lock (_gate) return _reason; }
    }

    public bool Trip(string reason)
    {
        lock (_gate)
        {
            if (_trippedAtUtc is not null) return false;
            _trippedAtUtc = DateTime.UtcNow;
            _reason = reason;
        }
        Tripped?.Invoke(reason);
        return true;
    }

    public bool Reset(string adminNote)
    {
        lock (_gate)
        {
            if (_trippedAtUtc is null) return false;
            _trippedAtUtc = null;
            _reason = null;
        }
        ResetBySupervisor?.Invoke(adminNote);
        return true;
    }
}
