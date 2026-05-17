using CryptoBot.Application.Ai;
using CryptoBot.Application.Backtesting;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Repositories;
using CryptoBot.Infrastructure.Ai;
using CryptoBot.Application.Backtesting.Search;
using CryptoBot.Infrastructure.Backtesting;
using CryptoBot.Infrastructure.Backtesting.Persistence;
using CryptoBot.Infrastructure.Backtesting.Search;
using CryptoBot.Infrastructure.Configuration;
using CryptoBot.Infrastructure.Exchange.BingX;
using CryptoBot.Infrastructure.ExchangeAccounts;
using CryptoBot.Infrastructure.Notifications;
using CryptoBot.Infrastructure.Persistence;
using CryptoBot.Infrastructure.Persistence.Repositories;
using CryptoBot.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoBot.Infrastructure;

/// <summary>
/// Infrastructure 層的 DI 註冊入口。
///
/// 目前範疇：Persistence（AppDbContext + Repositories + UnitOfWork）。
/// 後續將陸續加入 BingX（Exchange）與 Notifications（Discord / Console）註冊。
///
/// 原則：Application 層只看 Domain 的介面合約（<see cref="IOrderRepository"/> 等），
/// 不得看到任何 EF Core 型別 — 這個檔案是雙層之間的交接點。
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddPersistence(configuration);
        services.AddSingleton<IExchangeCredentialProvider, DbExchangeCredentialProvider>();
        services.AddBingXExchange(configuration);

        services.Configure<StrategySeedOptions>(
            configuration.GetSection(StrategySeedOptions.SectionName));
        services.AddSingleton<InitialStrategySeeder>();

        services.AddNotifications(configuration);
        services.AddBacktesting();
        services.AddSidekick(configuration);
        services.AddBayesianSidecar(configuration);
        return services;
    }

    /// <summary>
    /// S69 Phase 2 — 註冊 Bayesian Optuna sidecar 連線。
    ///
    /// Strategy 為 Transient — 每次 optimize job 取得新實例（內部持有 study_id 狀態 + IAsyncDisposable）；
    /// HttpClient 隨 strategy 共生死，job 結束後與 strategy 一起 dispose。Optimize 為使用者觸發的低頻
    /// 操作，不會造成 socket exhaustion，故未啟用 IHttpClientFactory（與 Discord/Gemini 同模式）。
    /// </summary>
    public static IServiceCollection AddBayesianSidecar(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BayesianSidecarOptions>(
            configuration.GetSection(BayesianSidecarOptions.SectionName));

        services.AddTransient<BayesianSearchStrategy>(sp =>
            new BayesianSearchStrategy(
                new HttpClient(),
                sp.GetRequiredService<IOptions<BayesianSidecarOptions>>(),
                sp.GetRequiredService<ILogger<BayesianSearchStrategy>>()));
        services.AddTransient<IAdaptiveSearchStrategy>(sp =>
            sp.GetRequiredService<BayesianSearchStrategy>());

        return services;
    }

    /// <summary>
    /// S74-D：CryptoBot Sidekick — 全域常駐 AI 對話 service 註冊（取代 legacy AddAiAdvisor）。
    ///
    /// 變更摘要：
    /// <list type="bullet">
    ///   <item>移除 <c>IAiAdvisorService</c> Replace 邏輯（GeminiAiAdvisorService / InteractiveCliAdvisorService 已刪）。</item>
    ///   <item>移除 <c>GeminiOptions</c> Configure（GeminiOptions.cs 已刪）。</item>
    ///   <item>保留 <c>InteractiveCliAdvisorOptions</c> Configure — <see cref="GlobalAiChatService"/>
    ///         仍依賴其 Executable / TimeoutSeconds（Sidekick 唯一可用 source、必須保留）。</item>
    ///   <item>保留 <c>IAiCredentialProvider</c> Singleton — <c>/api/ai/credentials</c> 端點與 ExchangeSettings.razor
    ///         的 Gemini API Key 管理 UI 仍由 <c>AiCredentialEndpoints.cs</c> 使用，與本 Sidekick 註冊獨立。</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddSidekick(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<InteractiveCliAdvisorOptions>(configuration.GetSection(InteractiveCliAdvisorOptions.SectionName));

        services.AddSingleton<IAiCredentialProvider, DbAiCredentialProvider>();

        // S75：gemini --acp 長連接 IPC client — Scoped per circuit（與 GlobalAiChatService 同生命週期）。
        // dispose 時關閉 stdin → process clean exit，fallback Kill(entireProcessTree:true)。
        services.AddScoped<IGeminiAcpClient, GeminiAcpClient>();

        // S74-C / S74-D / S75：CryptoBot Sidekick — Scoped per Blazor Server circuit。
        // 注入 IStrategyParameterKeyCatalog（介面位於 Application；具體 adapter 由 ConsoleApp 端註冊）+
        // IGeminiAcpClient (S75 重構, 取代 gemini -p single-shot) +
        // InteractiveCliAdvisorOptions（共用 gemini executable / timeout 設定）。
        services.AddScoped<IGlobalAiChatService, GlobalAiChatService>();

        return services;
    }

    /// <summary>
    /// 註冊回測相關的基礎設施服務（歷史資料下載、SQLite 存儲）。
    /// <see cref="BacktestSimulator"/> 與 <see cref="BacktestEngine"/> 具備短暫生命週期（一次回測作業），
    /// 因此不在此處註冊 — 由呼叫端（CLI / 測試）手動組裝後使用即可。
    /// </summary>
    public static IServiceCollection AddBacktesting(this IServiceCollection services)
    {
        services.AddScoped<IHistoricalKlineStore, EfHistoricalKlineStore>();
        services.AddSingleton<IHistoricalDataProvider, BingXHistoricalDataProvider>();
        return services;
    }

    /// <summary>
    /// 通知服務註冊：
    /// - Discord（Enabled=true 且 WebhookUrl 非空）→ 以 HttpClient-based 實作覆蓋 Application 層預設的 NoOp。
    /// - 未啟用或未配置 URL → 不註冊任何東西，Application 的 NoOp TryAddSingleton 會保留。
    ///
    /// 以 <c>Replace</c> 取代 <c>AddSingleton</c> 是為了把 Application 層用 TryAddSingleton 放下的 NoOp 覆寫掉；
    /// 用普通 Add 會留下兩份，DI 取最後一個但具體型別數會翻倍。
    /// </summary>
    public static IServiceCollection AddNotifications(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<DiscordOptions>(
            configuration.GetSection(DiscordOptions.SectionName));

        var discordCfg = configuration.GetSection(DiscordOptions.SectionName).Get<DiscordOptions>();
        if (discordCfg is { Enabled: true } && !string.IsNullOrWhiteSpace(discordCfg.WebhookUrl))
        {
            // 單一 Singleton HttpClient — webhook 呼叫極稀少、URL 固定，不需 HttpClientFactory 的池化管理。
            services.Replace(ServiceDescriptor.Singleton<INotificationService>(sp =>
                new DiscordNotificationService(
                    new HttpClient(),
                    sp.GetRequiredService<IOptions<DiscordOptions>>(),
                    sp.GetRequiredService<ILogger<DiscordNotificationService>>())));
        }

        return services;
    }

    /// <summary>
    /// 註冊 BingX 交易所實作。全部 Singleton — 內部持有長壽命的 REST + WebSocket 連線。
    ///
    /// 為什麼要「具體 + 介面」雙重註冊：
    /// - <see cref="BingXMarketDataStream"/> ctor 直接吃具體的 <see cref="BingXExchangeClient"/>
    ///   （非介面），因為 ListenKey REST 方法是 BingX 家族特有，不在 <see cref="IExchangeClient"/> 上。
    /// - 若只註冊介面，容器會為具體型別另建一份，造成兩個實例、兩組 listenKey / WS 連線。
    /// - Factory 寫法 (<c>sp =&gt; sp.GetRequiredService&lt;T&gt;()</c>) 讓介面指向同一個 Singleton。
    /// </summary>
    public static IServiceCollection AddBingXExchange(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BingXOptions>(
            configuration.GetSection(BingXOptions.SectionName));

        services.AddSingleton<BingXExchangeClient>();
        services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<BingXExchangeClient>());

        services.AddSingleton<BingXMarketDataStream>();
        services.AddSingleton<IMarketDataStream>(sp => sp.GetRequiredService<BingXMarketDataStream>());

        return services;
    }

    /// <summary>
    /// 註冊持久層：SQLite 上的 <see cref="AppDbContext"/>、三個 Repository、UnitOfWork。
    /// 皆以 <c>Scoped</c> 生命週期註冊（與 EF Core DbContext 同 scope）。
    /// </summary>
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<PersistenceOptions>(
            configuration.GetSection(PersistenceOptions.SectionName));

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var opts = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            options.UseSqlite(opts.ConnectionString);
        });

        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IPositionRepository, PositionRepository>();
        services.AddScoped<IStrategyRepository, StrategyRepository>();
        services.AddScoped<IExchangeAccountRepository, ExchangeAccountRepository>();
        services.AddScoped<IStrategyOptimizationSettingsRepository, StrategyOptimizationSettingsRepository>();
        services.AddScoped<IAiCredentialRepository, AiCredentialRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // S77 Bug 12: Domain event dispatcher + handlers (event-driven Position materialization)
        services.AddScoped<CryptoBot.Application.Common.DomainEvents.IDomainEventDispatcher,
            CryptoBot.Infrastructure.DomainEvents.DomainEventDispatcher>();
        services.AddScoped<CryptoBot.Application.Common.DomainEvents.IDomainEventHandler<CryptoBot.Domain.Events.OrderFilledEvent>,
            CryptoBot.Application.Trading.OrderFilledPositionMaterializer>();

        return services;
    }
}
