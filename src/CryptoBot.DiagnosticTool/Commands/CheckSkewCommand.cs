using CryptoBot.Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBot.DiagnosticTool.Commands;

/// <summary>
/// S66-D：本地時鐘 vs BingX 伺服器時鐘漂移即時檢查。
///
/// 與背景的 <c>NtpDriftMonitor</c> 不同，本指令**立即觸發一次同步**而非讀取上次的快照
/// 結果 — DiagnosticTool 通常在不啟動 ConsoleApp 的場景下使用，沒有「上次 sync」可讀。
///
/// 列印：
///   - 本地 UTC 時間
///   - 交易所 UTC 時間
///   - 偏差毫秒數（含正負號 — 正值＝伺服器領先、負值＝伺服器落後）
///   - 安全狀態（Safe / Warning / Unsafe）
/// </summary>
public sealed class CheckSkewCommand : IDiagnosticCommand
{
    private const double WarningThresholdMs = 500d;
    private const double RejectThresholdMs = 1000d;

    private readonly IServiceScopeFactory _scopeFactory;

    public CheckSkewCommand(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string Name => "s66d_check-skew";
    public IReadOnlyList<string> Aliases => new[] { "check-skew", "ntp" };
    public string Description => "Probe BingX server time and report local clock skew (Safe/Warning/Unsafe).";
    public string Usage => "s66d_check-skew";

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var exchange = sp.GetRequiredService<IExchangeClient>();

        Console.WriteLine("=== S66-D Clock Skew Inspector ===");
        Console.WriteLine($"Mode    : {exchange.CurrentMode}");
        Console.WriteLine();

        // 包夾測量 — 與 NtpDriftMonitor 演算法一致
        DateTime localBefore;
        DateTime serverTime;
        DateTime localAfter;

        try
        {
            localBefore = DateTime.UtcNow;
            serverTime = await exchange.GetServerTimeAsync(ct).ConfigureAwait(false);
            localAfter = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] GetServerTimeAsync failed: {ex.GetType().Name} -> {ex.Message}");
            return 5;
        }

        var localMidTicks = localBefore.Ticks + (localAfter.Ticks - localBefore.Ticks) / 2;
        var localMid = new DateTime(localMidTicks, DateTimeKind.Utc);
        var offset = serverTime - localMid;
        var roundTrip = localAfter - localBefore;
        var skewMs = (long)offset.TotalMilliseconds;
        var absMs = Math.Abs(skewMs);

        Console.WriteLine($"Local UTC (mid) : {localMid:yyyy-MM-dd HH:mm:ss.fff}");
        Console.WriteLine($"Server UTC      : {serverTime:yyyy-MM-dd HH:mm:ss.fff}");
        Console.WriteLine($"Round-trip      : {roundTrip.TotalMilliseconds:F0}ms");
        Console.WriteLine();
        Console.WriteLine($"Offset (server − local mid) : {skewMs:+0;-0;0}ms");
        Console.WriteLine($"  Direction : {(skewMs > 0 ? "Server is AHEAD of local" : skewMs < 0 ? "Server is BEHIND local" : "Perfectly aligned")}");
        Console.WriteLine();
        Console.WriteLine($"Thresholds      : Warning ±{WarningThresholdMs:F0}ms, Reject ±{RejectThresholdMs:F0}ms");

        string status;
        int exitCode;
        if (absMs > RejectThresholdMs)
        {
            status = $"❌ UNSAFE — exceeds reject threshold (RiskManager will block all orders)";
            exitCode = 6;
        }
        else if (absMs > WarningThresholdMs)
        {
            status = $"⚠️  WARNING — within reject threshold but elevated; investigate OS time sync";
            exitCode = 0;
        }
        else
        {
            status = "✅ SAFE";
            exitCode = 0;
        }

        Console.WriteLine($"Status          : {status}");
        Console.WriteLine();

        if (exitCode == 6)
        {
            Console.WriteLine("→ 立即動作：");
            Console.WriteLine("  1. 確認 OS 時鐘服務（Windows Time Service / chrony / ntpd）正常運作");
            Console.WriteLine("  2. 強制本地校時（Windows: w32tm /resync 或重新啟動 Windows Time）");
            Console.WriteLine("  3. 校正後重跑此指令確認 SAFE 才能恢復下單");
        }

        return exitCode;
    }
}
