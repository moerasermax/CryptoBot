namespace CryptoBot.Infrastructure.Configuration;

/// <summary>
/// S69 — Python sidecar 連線設定。對應 <c>appsettings.json</c> 的 <c>BayesianSidecar</c> 區塊。
/// </summary>
public sealed class BayesianSidecarOptions
{
    public const string SectionName = "BayesianSidecar";

    /// <summary>Python sidecar 的 base URL（含 host:port）。預設指向本機 5301 port。</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:5301";

    /// <summary>單次 HTTP 呼叫的逾時秒數。study/create 與 suggest 是即時的；過長代表 sidecar 卡死。</summary>
    public int TimeoutSeconds { get; set; } = 10;
}
