using CryptoBot.Application.Common;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Infrastructure.Exchange.BingX;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBot.DiagnosticTool.Commands;

/// <summary>
/// S31-T2 T1：巡檢 user-data WebSocket 的可連線性 —
/// 列出當前模式對應的 endpoint 家族、試取 listenKey 以證明金鑰 + endpoint 齊備、再釋放 key 乾淨收尾。
///
/// 刻意**不**啟動長連線或訂閱 — 這是「證據打單」指令，run-and-exit。
/// </summary>
public sealed class CheckWsCommand : IDiagnosticCommand
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CheckWsCommand(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    // 膠囊指定名稱為 s31_check-ws；同時提供較自然的 check-ws 作為 alias。
    public string Name => "s31_check-ws";
    public IReadOnlyList<string> Aliases => new[] { "check-ws", "ws" };
    public string Description => "Verify user-data WebSocket reachability: show endpoint family and acquire/release a listenKey.";
    public string Usage => "s31_check-ws";

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var exchange = scope.ServiceProvider.GetRequiredService<IExchangeClient>();

        Console.WriteLine("=== CryptoBot WebSocket Preflight ===");
        Console.WriteLine($"Mode:          {exchange.CurrentMode}");
        Console.WriteLine($"QuoteAsset:    {exchange.QuoteAsset}");
        Console.WriteLine($"Endpoint家族:  {EndpointHint(exchange.CurrentMode)}");
        Console.WriteLine();

        // BingX listenKey REST 呼叫走 Environment.Demo/Live — 成功即證明：
        //   (1) API Key 有效且有 User Data 權限
        //   (2) 當前模式對應的 REST endpoint 連得上
        //   (3) 後續 WS SubscribeToUserDataUpdates 可以使用此 key
        // 診斷工具專屬，直接抓具體 BingXExchangeClient（Clean Arch 例外 — 工具本就是深入檢查）。
        if (exchange is not BingXExchangeClient bx)
        {
            Console.WriteLine("[SKIP] Current IExchangeClient is not BingXExchangeClient — listenKey 檢查無法執行。");
            return 0;
        }

        Console.Write("ListenKey:     ");
        string? listenKey = null;
        try
        {
            listenKey = await bx.GetListenKeyAsync(ct);
            Console.WriteLine($"{MaskMiddle(listenKey)}  [GET OK]");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GET FAIL] {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("排查提示：");
            Console.WriteLine("  - 確認 DB 中 ActiveExchangeAccount 的 ApiKey/ApiSecret 正確");
            Console.WriteLine("  - 確認 API Key 已開啟 'User Data Stream' / 合約交易權限");
            Console.WriteLine($"  - 確認 TradingMode={exchange.CurrentMode} 對應的 endpoint 可達（防火牆 / DNS）");
            return 4;
        }

        // 釋放 key — 避免留一把 60 分鐘的殭屍 key 在 BingX 側。
        // 釋放失敗不改判斷結果：key 會自己過期，不影響生產。
        try
        {
            await bx.StopListenKeyAsync(listenKey, ct);
            Console.WriteLine($"ListenKey:     released cleanly");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ListenKey:     release failed — {ex.Message} (key will expire in 60m naturally)");
        }

        Console.WriteLine();
        Console.WriteLine("結論：user-data WS 鏈路就緒 — 生產端 BingXMarketDataStream.StartAsync 應能成功訂閱。");
        return 0;
    }

    private static string EndpointHint(TradingMode mode) => mode switch
    {
        TradingMode.Live => "BingXEnvironment.Live  (open-api.bingx.com / wss user-data)",
        TradingMode.Demo => "BingXEnvironment.Demo  (open-api-vst.bingx.com / wss user-data, VST 模擬盤)",
        _ => "Unknown",
    };

    private static string MaskMiddle(string key)
    {
        if (string.IsNullOrEmpty(key)) return "<empty>";
        if (key.Length <= 10) return new string('*', key.Length);
        return $"{key[..6]}…{key[^4..]}  (len={key.Length})";
    }
}
