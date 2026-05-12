using CryptoBot.Application;
using CryptoBot.Application.Ai;
using CryptoBot.Application.Realtime;
using CryptoBot.Application.RiskManagement;
using CryptoBot.Application.Strategies;
using CryptoBot.Application.Synchronization;
using CryptoBot.ConsoleApp.Api;
using CryptoBot.ConsoleApp.Components;
using CryptoBot.ConsoleApp.Lab;
using CryptoBot.ConsoleApp.Middleware;
using CryptoBot.ConsoleApp.Realtime;
using CryptoBot.ConsoleApp.Services;
using CryptoBot.Infrastructure;
using CryptoBot.Infrastructure.Configuration;
using CryptoBot.Infrastructure.Persistence;
using CryptoBot.Infrastructure.Seeding;
using Microsoft.AspNetCore.HttpOverrides;
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
    /// <summary>
    /// 僅供啟動 log 使用的顯示值。實際綁定由 <c>appsettings.json :: Kestrel:Endpoints</c>
    /// 掌握（S27 起外網部署需 <c>http://0.0.0.0:5000</c>）。
    /// </summary>
    public const string DefaultWebUrl = "http://localhost:5080";

    public static async Task<int> Main(string[] args)
    {
        // S66-C：Enrich.FromLogContext() 把 ILogger.BeginScope 推進來的 properties（含 TraceId）
        // 提到 log event 層級；outputTemplate 加 [TraceId:{TraceId}] 後綴讓終端機可一眼看到。
        // 沒有 TraceId 的 log（例如啟動期）會印 [TraceId:] 留白 — 非 K 線 tick 路徑的訊息不需追蹤。
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} [TraceId: {TraceId}]{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            Log.Information("CryptoBot 啟動中…");

            var isBacktest = args.Length > 0 &&
                             string.Equals(args[0], "backtest", StringComparison.OrdinalIgnoreCase);

            var app = BuildApp(args, isBacktest);
            await ApplyMigrationsIfConfiguredAsync(app.Services).ConfigureAwait(false);

            // S66-E：啟動 Pre-flight Check — 量測時鐘漂移並印 banner。
            // 若 appsettings:Startup:AbortIfSkewExceedsMs 設定且偏差超過該值，立即 Exit(1)。
            // 不在 backtest 路徑執行（回測用本地時間，不需與真實交易所對時）。
            if (!isBacktest)
            {
                var abortCode = await RunStartupPreflightAsync(app).ConfigureAwait(false);
                if (abortCode is int code) return code;
            }

            if (isBacktest)
            {
                Log.Information("偵測到 backtest 子命令 — 進入回測模式（不啟動 Web host）。");
                var code = await BacktestRunner.RunAsync(app.Services, args).ConfigureAwait(false);
                Log.Information("回測流程結束，exit code={Code}。", code);
                return code;
            }

            await SeedInitialStrategyAsync(app.Services).ConfigureAwait(false);

            var kestrelUrl = app.Configuration["Kestrel:Endpoints:Http:Url"];
            Log.Information("🌐 Web UI 指揮中心：{Url}", string.IsNullOrWhiteSpace(kestrelUrl) ? DefaultWebUrl : kestrelUrl);
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
        // S27：Kestrel 綁定改走 appsettings.json (Kestrel:Endpoints:Http:Url)，
        // 不再用 UseUrls 鎖死 localhost:5080 — 外網部署需要 0.0.0.0:5000。
        // IP 白名單由 IpWhitelistMiddleware 擋在最前面，杜絕非授權來源。
        builder.Services.Configure<IpWhitelistOptions>(
            builder.Configuration.GetSection(IpWhitelistOptions.SectionName));

        // S66-E：啟動 Pre-flight 檢查設定（AbortIfSkewExceedsMs nullable）
        builder.Services.Configure<StartupOptions>(
            builder.Configuration.GetSection(StartupOptions.SectionName));

        // S27-NGROK T1：ngrok 會把真正的客戶端 IP 放進 X-Forwarded-For，socket 上的 RemoteIpAddress
        // 只會是 127.0.0.1 / ngrok 代理的內部 IP。UseForwardedHeaders 會把 context.Connection.RemoteIpAddress
        // 改寫成 X-Forwarded-For 的第一跳（真實 client IP），之後 IpWhitelistMiddleware 才能拿到正確值。
        // Known{Networks,Proxies} 清空 — ngrok 代理 IP 是動態的，固定清單沒意義；若未來要限制只能走 ngrok，
        // 改在 appsettings 加 IpWhitelist 值（不是白名單代理）。
        // ForwardLimit = null：不管經過多少層代理（ngrok 可能有多跳），一律取 X-Forwarded-For 的最左側原始 IP。
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            options.ForwardLimit = null;
        });

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

            // S27：交易所 REST 延遲探測（每 15s → DashboardEventBus → GlobalStatusBar）
            builder.Services.AddHostedService<ExchangeHealthCheckService>();

            // S66-B：訂單對帳服務（每 1 min 巡檢 Pending / 殭屍訂單，與 AccountSynchronizer 互補的兜底機制）
            // S72 升級：本服務 line 119 已擴大涵蓋 PartiallyFilled，補殺長時間殭屍 partial 單。
            builder.Services.AddHostedService<OrderReconciliationService>();

            // S72：帳戶對帳週期性服務（每 5 min 跑 AccountSynchronizer.ReconcileAsync，含實證對帳）—
            // 補完原版「只啟動時跑一次」的盲區，杜絕 phantom close（IM §S72 鐵則）。
            builder.Services.AddHostedService<PeriodicAccountReconciliationService>();

            // S66-D：NTP 時鐘漂移監控（啟動立即 sync 一次 + 每 5 min tick；偏差 > 1000ms 由 RiskManager 攔截）
            builder.Services.AddHostedService<NtpDriftMonitor>();

            // S28 T1：日損熔斷監控（每 1 分鐘巡檢 → StopAll + 紫色 Discord 通知）
            builder.Services.AddHostedService<SafetyBreakerMonitor>();

            // S28 T1：將熔斷狀態事件橋接到 DashboardEventBus，讓 Blazor UI 即時解鎖/上鎖
            builder.Services.AddHostedService<SafetyBreakerDashboardBridge>();

            // 回測實驗室（參數優化）單例 + 內部閘門保證同時只跑一個 job
            builder.Services.AddSingleton<OptimizationOrchestrator>();

            // Lab 介面狀態艙：策略目錄 + 跨頁面狀態容器（訂閱 EventBus、做 ETA 計算）
            builder.Services.AddSingleton<StrategyCatalog>();
            builder.Services.AddSingleton<LabStateContainer>();

            // S74-C：把 ConsoleApp 的 StrategyCatalog adapter 成 Application 的
            // IStrategyParameterKeyCatalog — Infrastructure 的 GlobalAiChatService 透過此介面拿
            // union expected keys 餵 AiAdvicePayloadParser（不破壞 IRON ⑥ 四層相依）。
            builder.Services.AddSingleton<IStrategyParameterKeyCatalog, StrategyParameterKeyCatalogAdapter>();

            // S57 T1：IP 白名單管理 — 單例，內部用 SemaphoreSlim 序列化檔寫入避免 race。
            builder.Services.AddSingleton<IIpWhitelistService, IpWhitelistService>();

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
            // S27-NGROK T1：UseForwardedHeaders 必須先於 IpWhitelistMiddleware — 讓白名單檢查看到的是
            // X-Forwarded-For 的真實客戶端 IP，而不是 ngrok 代理的內部位址。
            app.UseForwardedHeaders();

            // S27：IP 白名單必須先於 StaticFiles / Routing，否則非授權來源能拉到靜態資源。
            app.UseMiddleware<IpWhitelistMiddleware>();

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
            app.MapAiCredentialEndpoints();
            app.MapAiAdvisorEndpoints();
            app.MapAdminEndpoints();            // S57
        }

        return app;
    }

    /// <summary>
    /// S66-E：啟動 Pre-flight 健檢 + ASCII banner + Abort 攔截。
    ///
    /// 回傳 <c>null</c> = 通過、可繼續啟動；回傳 <c>int</c> = 立即 return 該 exit code。
    /// </summary>
    private static async Task<int?> RunStartupPreflightAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var check = sp.GetRequiredService<IStartupHealthCheck>();
        var options = sp.GetRequiredService<IOptions<StartupOptions>>().Value;

        StartupCheckResult result;
        try
        {
            result = await check.RunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pre-flight check 自身拋例外（罕見）—— 跳過 banner，繼續啟動。");
            return null;
        }

        // 渲染 banner（在啟動 log 開頭就顯眼）
        StartupBannerRenderer.Render(result);

        // 偵測 abort 條件
        if (options.AbortIfSkewExceedsMs is int threshold
            && result.AbsoluteOffsetMs is long absMs
            && absMs > threshold)
        {
            Log.Fatal(
                "啟動中止：時鐘偏差 {SkewMs}ms 超過設定的 AbortIfSkewExceedsMs={Threshold}ms。" +
                "請執行系統校時（管理員 PowerShell: w32tm /resync /force）後重啟。",
                result.OffsetMs, threshold);
            return 1;
        }

        return null;
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
