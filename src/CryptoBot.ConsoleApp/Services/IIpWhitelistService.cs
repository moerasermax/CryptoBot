namespace CryptoBot.ConsoleApp.Services;

/// <summary>
/// S57 T1：動態 IP 白名單管理服務。讀取由 <see cref="Middleware.IpWhitelistOptions"/> 綁定的
/// 當前名單，並把使用者在 /admin 頁面的新增 / 移除操作寫回 <c>appsettings.json</c>。
///
/// 熱載語意：appsettings.json 是由 ASP.NET Core 的 ConfigurationBuilder 加 <c>reloadOnChange:true</c>
/// 載入的，<c>IpWhitelistMiddleware</c> 用 <c>IOptionsMonitor</c> 訂閱該檔變動——寫回後幾秒內
/// Middleware 會自動拿到新名單，不必重啟服務。
/// </summary>
public interface IIpWhitelistService
{
    /// <summary>回傳目前生效的白名單快照（從 IOptionsMonitor.CurrentValue 讀）。</summary>
    IReadOnlyList<string> GetAllowed();

    /// <summary>
    /// 把一組 IP 加入白名單並寫回 appsettings.json。回傳：
    /// <list type="bullet">
    ///   <item><see cref="WhitelistMutationResult.Added"/> — 成功寫入</item>
    ///   <item><see cref="WhitelistMutationResult.AlreadyExists"/> — 該 IP 已在名單</item>
    ///   <item><see cref="WhitelistMutationResult.InvalidFormat"/> — 不是合法 IPv4/IPv6</item>
    /// </list>
    /// </summary>
    Task<WhitelistMutationResult> AddAsync(string ip, CancellationToken ct = default);

    /// <summary>
    /// 從白名單移除一組 IP 並寫回 appsettings.json。找不到該條時回傳 <c>NotFound</c>。
    /// </summary>
    Task<WhitelistMutationResult> RemoveAsync(string ip, CancellationToken ct = default);
}

/// <summary>S57 T1：白名單寫入動作的結果碼。</summary>
public enum WhitelistMutationResult
{
    Added,
    Removed,
    AlreadyExists,
    NotFound,
    InvalidFormat,
}
