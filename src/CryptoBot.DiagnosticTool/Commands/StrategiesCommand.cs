using CryptoBot.Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBot.DiagnosticTool.Commands;

/// <summary>
/// <c>strategies</c>：列出 DB 中所有策略的身分、運行狀態與關鍵配置。
/// 讓 PM 不必開 Dashboard 也能巡檢策略是否就緒或卡在 Stopped。
/// </summary>
public sealed class StrategiesCommand : IDiagnosticCommand
{
    private readonly IServiceScopeFactory _scopeFactory;

    public StrategiesCommand(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string Name => "strategies";
    public IReadOnlyList<string> Aliases => new[] { "list", "ls" };
    public string Description => "List all strategies from DB with status and key configuration.";
    public string Usage => "strategies";

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var strategyRepo = scope.ServiceProvider.GetRequiredService<IStrategyRepository>();

        var all = await strategyRepo.GetAllAsync(ct);

        Console.WriteLine("=== Strategies ===");
        if (all.Count == 0)
        {
            Console.WriteLine("(no strategies in DB — 確認 cryptobot.db 路徑是否正確)");
            return 0;
        }

        // 欄寬依實際資料動態調整 — 不寫死省得長名字溢出看不清。
        var rows = all.Select(s => new Row(
            Name: s.Name,
            Type: s.StrategyType.ToString(),
            SymbolInterval: $"{s.Configuration.Symbol.BingXFormat} @ {s.Configuration.Interval}",
            Status: s.Status.ToString(),
            Leverage: $"{s.Configuration.Leverage.Value}x",
            Risk: $"{s.Configuration.RiskPerTradePercent:P2}",
            MaxPos: s.Configuration.MaxConcurrentPositions.ToString())).ToList();

        static int Width(IEnumerable<Row> items, Func<Row, string> selector, string header)
        {
            var max = header.Length;
            foreach (var r in items)
            {
                var len = selector(r).Length;
                if (len > max) max = len;
            }
            return max;
        }

        var wName = Width(rows, r => r.Name, "Name");
        var wType = Width(rows, r => r.Type, "Type");
        var wSym  = Width(rows, r => r.SymbolInterval, "Symbol@Interval");
        var wStat = Width(rows, r => r.Status, "Status");
        var wLev  = Width(rows, r => r.Leverage, "Lev");
        var wRisk = Width(rows, r => r.Risk, "Risk");
        var wMax  = Width(rows, r => r.MaxPos, "MaxPos");

        string Format(string name, string type, string sym, string stat, string lev, string risk, string max) =>
            $"{name.PadRight(wName)}  {type.PadRight(wType)}  {sym.PadRight(wSym)}  {stat.PadRight(wStat)}  {lev.PadRight(wLev)}  {risk.PadRight(wRisk)}  {max.PadRight(wMax)}";

        var header = Format("Name", "Type", "Symbol@Interval", "Status", "Lev", "Risk", "MaxPos");
        Console.WriteLine(header);
        Console.WriteLine(new string('-', header.Length));
        foreach (var r in rows)
            Console.WriteLine(Format(r.Name, r.Type, r.SymbolInterval, r.Status, r.Leverage, r.Risk, r.MaxPos));

        Console.WriteLine();
        Console.WriteLine($"Total: {rows.Count}");
        return 0;
    }

    private sealed record Row(
        string Name,
        string Type,
        string SymbolInterval,
        string Status,
        string Leverage,
        string Risk,
        string MaxPos);
}
