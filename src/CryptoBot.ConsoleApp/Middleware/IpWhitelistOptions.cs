namespace CryptoBot.ConsoleApp.Middleware;

/// <summary>
/// 綁到 appsettings.json 的 <c>Security</c> 段。
/// <see cref="AllowedIPs"/> 必須是 IPv4 / IPv6 的完整位址字串（不支援 CIDR — 若未來需要
/// 網段白名單，這裡改型別為 <c>List&lt;string&gt;</c> 並在 Middleware 裡加 parser）。
/// 空清單視為「不啟用白名單」— 任何來源都放行，對 dev 環境友善。
/// </summary>
public sealed class IpWhitelistOptions
{
    public const string SectionName = "Security";

    public List<string> AllowedIPs { get; set; } = new();
}
