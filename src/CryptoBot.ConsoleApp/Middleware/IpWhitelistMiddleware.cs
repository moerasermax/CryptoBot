using System.Net;
using Microsoft.Extensions.Options;

namespace CryptoBot.ConsoleApp.Middleware;

/// <summary>
/// 外網防護第一道閘：來源 IP 不在 <see cref="IpWhitelistOptions.AllowedIPs"/> 名單內就直接
/// 回 403，連 Blazor 的 SignalR handshake 與 Minimal API 路由都不會看到該請求。
///
/// <para>
/// 位置：必須 <b>先於</b> <c>UseStaticFiles</c> / <c>UseRouting</c> 註冊 — 否則靜態檔案
/// 會先被送出，白名單就失效了（S27 VCP-Security 明文要求）。
/// </para>
///
/// <para>
/// IPv4-mapped IPv6（<c>::ffff:127.0.0.1</c>）會被 <see cref="IPAddress.MapToIPv4"/>
/// 正規化後比對 — 這樣 <c>127.0.0.1</c> 與 <c>::ffff:127.0.0.1</c> 都能通過白名單為
/// <c>127.0.0.1</c> 的檢查（Kestrel 在 dual-stack socket 上經常回前者）。
/// </para>
///
/// <para>
/// 空白名單時 fail-open（放行全部）— 避免 dev 環境配置疏漏就把自己鎖在外面；正式部署
/// 必須在 appsettings 明確列出來源 IP。
/// </para>
/// </summary>
public sealed class IpWhitelistMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<IpWhitelistOptions> _options;
    private readonly ILogger<IpWhitelistMiddleware> _logger;

    public IpWhitelistMiddleware(
        RequestDelegate next,
        IOptionsMonitor<IpWhitelistOptions> options,
        ILogger<IpWhitelistMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var allowed = _options.CurrentValue.AllowedIPs;
        var remote = context.Connection.RemoteIpAddress;

        // S32-S35-REVISED T0：黃字診斷 log — 讓使用者從外網（ngrok）連入時能即刻看到
        // (a) Kestrel 收到的 Client IP、(b) ngrok 代理塞的 X-Forwarded-For、(c) 目前白名單筆數。
        // 手機連入 403 時多半是因為外網 IP 沒加入名單；這行 log 讓你直接 copy 字串丟進 appsettings。
        _logger.LogWarning(
            "🛡 Whitelist Check: Client={Ip}, XFF={Xff}, AllowedCount={Count}",
            remote, context.Request.Headers["X-Forwarded-For"].ToString(), allowed?.Count ?? 0);

        if (allowed is null || allowed.Count == 0)
        {
            // 空名單 = 不啟用 — 直接放行（log 一次 debug，避免被忘了配置）
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (remote is null)
        {
            _logger.LogWarning(
                "IP whitelist reject: no RemoteIpAddress on request {Path}", context.Request.Path);
            await WriteForbiddenAsync(context).ConfigureAwait(false);
            return;
        }

        if (IsAllowed(remote, allowed))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning(
            "🛡 IP whitelist REJECT {Ip} → {Method} {Path} (allowed={Count})",
            remote, context.Request.Method, context.Request.Path, allowed.Count);
        await WriteForbiddenAsync(context).ConfigureAwait(false);
    }

    private static bool IsAllowed(IPAddress remote, IReadOnlyList<string> allowed)
    {
        var normalizedRemote = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;

        foreach (var entry in allowed)
        {
            if (!IPAddress.TryParse(entry, out var configured)) continue;
            var normalizedConfigured = configured.IsIPv4MappedToIPv6
                ? configured.MapToIPv4()
                : configured;

            if (normalizedConfigured.Equals(normalizedRemote)) return true;
        }
        return false;
    }

    private static async Task WriteForbiddenAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(
            "403 Forbidden: source IP not in whitelist.").ConfigureAwait(false);
    }
}
