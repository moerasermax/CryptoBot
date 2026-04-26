namespace CryptoBot.Application.Realtime;

/// <summary>
/// 交易所連線健康度心跳。每 <see cref="ExchangeHealthCheckService"/> 採樣一次後由
/// <c>DashboardEventBus.ExchangeHealthUpdated</c> 推給 UI 的 <c>GlobalStatusBar</c>。
///
/// <para>
/// <see cref="LatencyMs"/> 是 REST 探測往返時間（一次輕量 BookTicker 呼叫），而非 WS 延遲 —
/// WS 斷線時 <see cref="IsHealthy"/> 會是 <c>false</c> 並附 <see cref="ErrorMessage"/>。
/// </para>
/// </summary>
public sealed record ExchangeHealthUpdate(
    DateTime Timestamp,
    string ExchangeName,
    int LatencyMs,
    bool IsHealthy,
    string? ErrorMessage);
