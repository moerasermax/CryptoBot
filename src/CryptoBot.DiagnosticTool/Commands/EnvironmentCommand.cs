using CryptoBot.Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBot.DiagnosticTool.Commands;

/// <summary>
/// <c>env</c>：列出當前 API 模式 / QuoteAsset / 餘額 / 持倉讀取健康狀態。
/// 刻意不做破壞性的交易權限探測 — 真正的 Trading Enabled 要靠 Live 首單驗證，提示欄位明示。
/// </summary>
public sealed class EnvironmentCommand : IDiagnosticCommand
{
    private readonly IServiceScopeFactory _scopeFactory;

    public EnvironmentCommand(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string Name => "env";
    public IReadOnlyList<string> Aliases => new[] { "environment" };
    public string Description => "Print current API mode / quote asset / balance / read-API health.";
    public string Usage => "env";

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var exchange = scope.ServiceProvider.GetRequiredService<IExchangeClient>();

        Console.WriteLine("=== CryptoBot Environment Inspector ===");
        Console.WriteLine($"Exchange:     {exchange.ExchangeName}");
        Console.WriteLine($"Mode:         {exchange.CurrentMode}  (QuoteAsset={exchange.QuoteAsset})");

        Console.Write("Balance:      ");
        try
        {
            var bal = await exchange.GetFuturesBalanceAsync(ct: ct);
            Console.WriteLine($"{bal:F4} {exchange.QuoteAsset}  [READ OK]");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[READ FAIL] {ex.Message}");
        }

        bool readApiOk;
        Console.Write("Positions:    ");
        try
        {
            var positions = await exchange.GetOpenPositionsAsync(ct);
            Console.WriteLine($"{positions.Count} open  [READ OK]");
            readApiOk = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[READ FAIL] {ex.Message}");
            readApiOk = false;
        }

        Console.WriteLine($"Trading:      {(readApiOk ? "READ-OK (write 需實單驗證 — 非破壞性無法判定)" : "KEY/PERMISSION FAIL")}");
        return 0;
    }
}
