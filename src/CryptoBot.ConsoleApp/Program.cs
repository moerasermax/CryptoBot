using CryptoBot.Application;
using CryptoBot.Application.Realtime;
using CryptoBot.Application.Strategies;
using CryptoBot.ConsoleApp.Api;
using CryptoBot.ConsoleApp.Components;
using CryptoBot.ConsoleApp.Lab;
using CryptoBot.ConsoleApp.Realtime;
using CryptoBot.ConsoleApp.Services;
using CryptoBot.Infrastructure;
using CryptoBot.Infrastructure.Configuration;
using CryptoBot.Infrastructure.Persistence;
using CryptoBot.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;

namespace CryptoBot.ConsoleApp;

/// <summary>
/// S15/S16 後的啟動流程：
///
/// <list type="bullet">
///   <item>一律用 <see cref="WebApplication"/> 組 host — 回測 CLI 模式不呼叫 <c>RunAsync</c>，
///         所以 Kestrel 根本不會起來，回測跑完就 return。</item>
///   <item>一般模式啟動 Web UI（Blazor Server on <c>http://localhost:5080</c>）、
///         Minimal APIs、SignalR Hub、<see cref="StrategyRuntimeHostedService"/> 與
///         <see cref="DashboardPushService"/> 背景推播。</item>
/// </list>
/// </summary>
public static class Program
{
    public const string DefaultWebUrl = "http://localhost:5080";

    public static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.Console()
            .CreateLogger();

        try
        {
            Log.Information("CryptoBot 啟動中…");

            var isBacktest = args.Length > 0 &&
                             string.Equals(args[0], "backtest", StringComparison.OrdinalIgnoreCase);

            var app = BuildApp(args, isBacktest);
            await ApplyMigrationsIfConfiguredAsync(app.Services).ConfigureAwait(false);

            if (isBacktest)
            {
                Log.Information("偵測到 backtest 子命令 — 進入回測模式（不啟動 Web host）。");
                var code = await BacktestRunner.RunAsync(app.Services, args).ConfigureAwait(false);
                Log.Information("回測流程結束，exit code={Code}。", code);
                return code;
            }

            await SeedInitialStrategyAsync(app.Services).ConfigureAwait(false);

            Log.Information("🌐 Web UI 指揮中心：{Url}", DefaultWebUrl);
            await app.RunAsync().ConfigureAwait(false);

            Log.Information("CryptoBot 已正常關閉。");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "啟動流程致命例外。");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static WebApplication BuildApp(string[] args, bool isBacktest)
    {
        var builder = WebApplication.CreateBuilder(args);

        // 讓 appsettings.json 不管 CWD 是哪裡都抓得到（cf. csproj 的 AfterBuild Target）
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables();

        builder.Host.UseSerilog();
        builder.WebHost.UseUrls(DefaultWebUrl);

        // 核心三層（保持與之前 console 版一致）
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);

        // 回測模式下不需要啟動 live 策略、背景推播、Hub — 全部略過，省記憶體與 API 限額。
        if (!isBacktest)
        {
            // Web host 才把 NullRealtimeBroadcaster Replace 成 SignalR 版本
            builder.Services.AddSingleton<DashboardEventBus>();
            builder.Services.Replace(ServiceDescriptor.Singleton<IRealtimeBroadcaster, SignalRRealtimeBroadcaster>());

            // 策略執行主迴圈（market data + synchronizer + executors）
            builder.Services.AddSingleton<StrategyRuntimeHostedService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<StrategyRuntimeHostedService>());
            builder.Services.AddSingleton<IStrategyRuntimeController>(sp =>
                sp.GetRequiredService<StrategyRuntimeHostedService>());

            // 儀表板每 2 秒心跳
            builder.Services.AddScoped<DashboardStatsService>();
            builder.Services.AddHostedService<DashboardPushService>();

            // 回測實驗室（參數優化）單例 + 內部閘門保證同時只跑一個 job
            builder.Services.AddSingleton<OptimizationOrchestrator>();

            // Lab 介面狀態艙：策略目錄 + 跨頁面狀態容器（訂閱 EventBus、做 ETA 計算）
            builder.Services.AddSingleton<StrategyCatalog>();
            builder.Services.AddSingleton<LabStateContainer>();

            // Lab 頁面用 HttpClientFactory 回打自己的 Minimal API（集中驗證 + 狀態碼邏輯）
            builder.Services.AddHttpClient();

            // ASP.NET Core: Blazor + SignalR + Minimal APIs
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();
            builder.Services.AddSignalR();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddAntiforgery();
        }

        var app = builder.Build();

        if (!isBacktest)
        {
            app.UseStaticFiles();
            app.UseRouting();
            app.UseAntiforgery();

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.MapHub<TradeHub>("/hub/trade");

            app.MapDashboardEndpoints();
            app.MapStrategyEndpoints();
            app.MapLabEndpoints();
            app.MapExchangeAccountEndpoints();
        }

        return app;
    }

    private static async Task ApplyMigrationsIfConfiguredAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var opts = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
        if (!opts.AutoMigrateOnStartup)
        {
            Log.Information("AutoMigrate 關閉，略過 migration。");
            return;
        }

        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);
    }

    private static async Task SeedInitialStrategyAsync(IServiceProvider services)
    {
        var seeder = services.GetRequiredService<InitialStrategySeeder>();
        await seeder.SeedAsync().ConfigureAwait(false);
    }
}
