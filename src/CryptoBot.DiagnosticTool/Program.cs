using CryptoBot.Application;
using CryptoBot.DiagnosticTool.Commands;
using CryptoBot.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoBot.DiagnosticTool;

/// <summary>
/// S59-ADD T1：Dispatcher-only 進入點 — 子命令走 <see cref="IDiagnosticCommand"/> 抽象，
/// 新增命令只要在 <see cref="RegisterCommands"/> 多一行即可，進入點本身不動。
///
/// 刻意**不**啟動 HostedService — 單次 CLI 執行跑完即退。必須在 ConsoleApp 目錄下執行（或
/// 執行時 cwd 指到 ConsoleApp），因為 SQLite 連線字串是相對路徑 <c>Data Source=cryptobot.db</c>。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // S63-UX：Windows 終端機預設 OEM 代碼頁（cp950/cp437）會把 UTF-8 中文字與全形符號（「落在」「進行中」「→」）
        // 吐成亂碼。強制輸出 UTF-8 與 Program 啟動後所有 WriteLine 對齊；不動 InputEncoding，
        // 因為本工具不讀 stdin。
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var services = BuildServices();
        var commands = services.GetRequiredService<IEnumerable<IDiagnosticCommand>>().ToList();
        var dispatch = BuildDispatch(commands);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(commands);
            return 0;
        }

        var key = args[0].ToLowerInvariant();
        if (!dispatch.TryGetValue(key, out var command))
        {
            Console.Error.WriteLine($"Unknown command: {args[0]}");
            PrintUsage(commands);
            return 2;
        }

        try
        {
            return await command.RunAsync(args.Skip(1).ToArray(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 99;
        }
    }

    private static IServiceProvider BuildServices()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        }).SetMinimumLevel(LogLevel.Warning));

        services.AddApplication();
        services.AddInfrastructure(config);

        RegisterCommands(services);

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>
    /// 新增診斷命令只要在這裡多加一行 — 進入點、dispatcher、help 文字全部自動跟上。
    /// </summary>
    private static void RegisterCommands(IServiceCollection services)
    {
        services.AddSingleton<IDiagnosticCommand, EnvironmentCommand>();
        services.AddSingleton<IDiagnosticCommand, SizingCommand>();
        services.AddSingleton<IDiagnosticCommand, StrategiesCommand>();
        services.AddSingleton<IDiagnosticCommand, CheckWsCommand>();
        services.AddSingleton<IDiagnosticCommand, CheckMtfCommand>();
        services.AddSingleton<IDiagnosticCommand, CheckKlineCommand>();
        services.AddSingleton<IDiagnosticCommand, SyncOrdersCommand>();
    }

    private static IReadOnlyDictionary<string, IDiagnosticCommand> BuildDispatch(
        IReadOnlyList<IDiagnosticCommand> commands)
    {
        var map = new Dictionary<string, IDiagnosticCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in commands)
        {
            map[cmd.Name] = cmd;
            foreach (var alias in cmd.Aliases)
                map[alias] = cmd;
        }
        return map;
    }

    private static void PrintUsage(IReadOnlyList<IDiagnosticCommand> commands)
    {
        Console.WriteLine("CryptoBot Diagnostic Tool (S59)");
        Console.WriteLine();
        Console.WriteLine("Run from src/CryptoBot.ConsoleApp (so cryptobot.db and appsettings.json are visible):");
        Console.WriteLine("  dotnet run --project ../CryptoBot.DiagnosticTool -- <command> [args]");
        Console.WriteLine();
        Console.WriteLine("Commands:");

        var nameWidth = commands.Max(c => c.Usage.Length);
        foreach (var cmd in commands.OrderBy(c => c.Name))
        {
            var aliasPart = cmd.Aliases.Count > 0 ? $"  (aliases: {string.Join(", ", cmd.Aliases)})" : string.Empty;
            Console.WriteLine($"  {cmd.Usage.PadRight(nameWidth)}  {cmd.Description}{aliasPart}");
        }
    }
}
