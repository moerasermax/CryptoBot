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

        // S71：cwd 自癒 — 若從 repo 根 / 任意目錄執行（dotnet run --project ...），
        // SQLite 相對路徑 Data Source=cryptobot.db 會解析到錯誤位置（缺 ExchangeAccounts 表），
        // 在此自動切回 ConsoleApp 目錄並廣播（依 IRON ⑥ 風控透明化精神）。
        TryRelocateCwdToConsoleApp();

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

    /// <summary>
    /// S71 cwd 自癒：當前工作目錄缺 cryptobot.db 時，依優先序探測 ConsoleApp 候選位置，
    /// 找到（同時含 cryptobot.db 與 appsettings.json 才視為合格 ConsoleApp 目錄）即切過去。
    /// 失敗時刻意不拋例外 — 維持向後相容（讓既有 PrintUsage 提示繼續引導使用者）。
    /// </summary>
    private static void TryRelocateCwdToConsoleApp()
    {
        var currentCwd = Directory.GetCurrentDirectory();

        // cwd 已是 ConsoleApp（含 cryptobot.db）即不動 — 向後相容路徑。
        if (File.Exists(Path.Combine(currentCwd, "cryptobot.db")))
        {
            return;
        }

        // 候選相對路徑（由近至遠覆蓋常見執行情境：repo 根 / DiagnosticTool 旁 / src 同層 / 其他兄弟）
        string[] candidates =
        {
            "src/CryptoBot.ConsoleApp",
            "CryptoBot/src/CryptoBot.ConsoleApp",
            "../CryptoBot.ConsoleApp",
            "../src/CryptoBot.ConsoleApp",
            "../../src/CryptoBot.ConsoleApp",
        };

        foreach (var rel in candidates)
        {
            var abs = Path.GetFullPath(Path.Combine(currentCwd, rel));
            if (File.Exists(Path.Combine(abs, "cryptobot.db")) &&
                File.Exists(Path.Combine(abs, "appsettings.json")))
            {
                Directory.SetCurrentDirectory(abs);
                Console.WriteLine($"[CWD] relocated from {currentCwd} to {abs}");
                return;
            }
        }

        Console.WriteLine($"[CWD] could not relocate (no ConsoleApp candidate found from {currentCwd})");
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
        services.AddSingleton<IDiagnosticCommand, ProbeTradesCommand>();
        services.AddSingleton<IDiagnosticCommand, CheckOrderCommand>();
        services.AddSingleton<IDiagnosticCommand, ProbeBingxCommand>();
        services.AddSingleton<IDiagnosticCommand, CheckSkewCommand>();
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
