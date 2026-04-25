using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoBot.ConsoleApp.Middleware;
using Microsoft.Extensions.Options;

namespace CryptoBot.ConsoleApp.Services;

/// <summary>
/// S57 T1：動態 IP 白名單服務。寫回 appsettings.json 時：
/// <list type="bullet">
///   <item>用 <see cref="JsonNode"/> 解析 → 修改 → 序列化，保留其他 section 不動</item>
///   <item><c>WriteIndented = true</c>（2 空格）統一縮排樣式（PM S57 交付要求）</item>
///   <item>原子寫入：先寫 <c>.tmp</c> 再 <see cref="File.Move(string, string, bool)"/> 覆蓋 —
///         避免部分寫入被 IOptionsMonitor 讀到造成解析失敗</item>
///   <item><see cref="SemaphoreSlim"/> 序列化併發寫入（同時多個 admin 請求不會互相洗掉）</item>
/// </list>
///
/// appsettings.json 的實體檔位置：<see cref="IHostEnvironment.ContentRootPath"/> 下的那份 —
/// 即 <c>Program.cs</c> 用 <c>AddJsonFile("appsettings.json", reloadOnChange: true)</c> 訂閱
/// 的那一份，才是 IOptionsMonitor 實際監聽的檔案。
/// </summary>
public sealed class IpWhitelistService : IIpWhitelistService
{
    private readonly string _appsettingsPath;
    private readonly IOptionsMonitor<IpWhitelistOptions> _options;
    private readonly ILogger<IpWhitelistService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        // JsonNode → ToJsonString 預設會 escape 非 ASCII；這裡保留原樣，
        // 例如中文註解或 IPv6 冒號都不會被轉成 \uXXXX。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions ReadOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public IpWhitelistService(
        IHostEnvironment env,
        IOptionsMonitor<IpWhitelistOptions> options,
        ILogger<IpWhitelistService> logger)
    {
        _appsettingsPath = Path.Combine(env.ContentRootPath, "appsettings.json");
        _options = options;
        _logger = logger;
    }

    public IReadOnlyList<string> GetAllowed()
        => _options.CurrentValue.AllowedIPs.ToList();

    public Task<WhitelistMutationResult> AddAsync(string ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out _))
            return Task.FromResult(WhitelistMutationResult.InvalidFormat);

        return MutateAsync(ip.Trim(), list =>
        {
            if (list.Any(x => string.Equals(x, ip.Trim(), StringComparison.OrdinalIgnoreCase)))
                return WhitelistMutationResult.AlreadyExists;
            list.Add(ip.Trim());
            return WhitelistMutationResult.Added;
        }, ct);
    }

    public Task<WhitelistMutationResult> RemoveAsync(string ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return Task.FromResult(WhitelistMutationResult.NotFound);

        return MutateAsync(ip.Trim(), list =>
        {
            var idx = list.FindIndex(x => string.Equals(x, ip.Trim(), StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return WhitelistMutationResult.NotFound;
            list.RemoveAt(idx);
            return WhitelistMutationResult.Removed;
        }, ct);
    }

    private async Task<WhitelistMutationResult> MutateAsync(
        string ipForLog,
        Func<List<string>, WhitelistMutationResult> mutator,
        CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1) 讀原始 JSON（保留其他 section）
            string raw;
            await using (var fs = new FileStream(
                _appsettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sr = new StreamReader(fs))
            {
                raw = await sr.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            var root = JsonNode.Parse(raw, documentOptions: ReadOpts) as JsonObject
                ?? throw new InvalidOperationException("appsettings.json root is not an object.");

            // 2) 找到（或新增）Security.AllowedIPs 節
            if (root["Security"] is not JsonObject securityNode)
            {
                securityNode = new JsonObject();
                root["Security"] = securityNode;
            }

            var currentList = new List<string>();
            if (securityNode["AllowedIPs"] is JsonArray arr)
            {
                foreach (var v in arr)
                {
                    if (v is not null) currentList.Add(v.GetValue<string>());
                }
            }

            // 3) 應用 mutator — 不改動也能短路回傳（AlreadyExists / NotFound）
            var outcome = mutator(currentList);
            if (outcome is WhitelistMutationResult.AlreadyExists or WhitelistMutationResult.NotFound)
            {
                _logger.LogInformation(
                    "IpWhitelistService: {Ip} → {Outcome} (no-op, file not rewritten).",
                    ipForLog, outcome);
                return outcome;
            }

            // 4) 回寫 AllowedIPs
            var newArr = new JsonArray();
            foreach (var s in currentList) newArr.Add(s);
            securityNode["AllowedIPs"] = newArr;

            // 5) 序列化 + 原子寫入（寫 tmp → move overwrite）
            var formatted = root.ToJsonString(WriteOpts);
            // 確保檔尾換行 — POSIX / git diff 友善
            if (!formatted.EndsWith('\n')) formatted += Environment.NewLine;

            var tmpPath = _appsettingsPath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, formatted, ct).ConfigureAwait(false);
            File.Move(tmpPath, _appsettingsPath, overwrite: true);

            _logger.LogInformation(
                "IpWhitelistService: {Ip} → {Outcome}; appsettings.json now lists {Count} IPs.",
                ipForLog, outcome, currentList.Count);
            return outcome;
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
