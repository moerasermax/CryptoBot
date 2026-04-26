using CryptoBot.Application.Ai;
using CryptoBot.Application.Common;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Application.Notifications;
using CryptoBot.Application.Realtime;
using CryptoBot.Application.RiskManagement;
using CryptoBot.Application.Strategies;
using CryptoBot.Application.Strategies.Arbitrage;
using CryptoBot.Application.Strategies.B46RsiBb;
using CryptoBot.Application.Strategies.MeanReversion;
using CryptoBot.Application.Strategies.MtfMeanReversion;
using CryptoBot.Application.Strategies.PriceAction;
using CryptoBot.Application.Strategies.SmaCrossover;
using CryptoBot.Application.Strategies.TrendFollowing;
using CryptoBot.Application.Synchronization;
using CryptoBot.Application.Trading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoBot.Application;

/// <summary>
/// Application 層 DI 入口。
///
/// 註冊目標：
/// - <see cref="RiskLimits"/>（Singleton，預設 Moderate；未來可由 appsettings 覆蓋）
/// - <see cref="IStrategyCooldownTracker"/>（Singleton — 跨策略共用的冷卻狀態表）
/// - <see cref="IRiskManager"/>（Scoped — 內部使用 Scoped 的 <see cref="IPositionRepository"/>）
/// - <see cref="IOrderSizer"/>（Scoped — 保持與 RiskManager 同生命週期便於組合使用）
/// - <see cref="IStrategyExecutorFactory"/>（Singleton — 每個策略 Executor 由此工廠產生，
///   Executor 內部自己開 DI scope 處理每根 K 線）
/// - 具體 <see cref="IStrategy"/> 實作與 <see cref="IStrategyFactory"/>（Singleton — 無狀態可共用）
/// - <see cref="IAccountSynchronizer"/>（Singleton — 持有 WS handler 生命週期）
/// - <see cref="StrategyRuntimeHostedService"/> 由 ConsoleApp 那層用
///   <c>AddHostedService&lt;&gt;</c> 註冊，因為它是 host 相關的執行主體。
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(RiskLimits.Moderate);
        services.AddSingleton<IStrategyCooldownTracker, StrategyCooldownTracker>();

        // S28 T1：日損熔斷共享狀態（Singleton — 跨 API / Monitor / Host 同步）
        services.AddSingleton<ISafetyBreakerState, SafetyBreakerState>();

        // S66-D：本地與交易所時鐘漂移狀態（Singleton — NtpDriftMonitor 寫入、RiskManager 讀取）
        services.AddSingleton<IClockSkewState, ClockSkewState>();

        // S66-E：包夾測量 service（Singleton — 無狀態 hash 計算，跨 monitor / startup-check / diagnostic 共用）
        services.AddSingleton<ISkewMeasurementService, SkewMeasurementService>();

        // S66-E：啟動 Pre-flight 健檢（Singleton — 純讀，跨 ConsoleApp 啟動程序呼叫一次）
        services.AddSingleton<IStartupHealthCheck, StartupSkewCheck>();

        services.AddScoped<IRiskManager, RiskManager>();
        services.AddScoped<IOrderSizer, OrderSizer>();

        // S66-A：決定性 ClientOrderId 生成器 — 無狀態 hash 計算，Singleton 合適。
        services.AddSingleton<IClientOrderIdGenerator, DeterministicClientOrderIdGenerator>();

        services.AddSingleton<IStrategyExecutorFactory, StrategyExecutorFactory>();

        // 具體策略 — 無狀態，全部 Singleton；StrategyFactory 透過 IEnumerable<IStrategy> 注入取得全部實例
        services.AddSingleton<IStrategy, TrendFollowingStrategy>();
        services.AddSingleton<IStrategy, MeanReversionStrategy>();
        services.AddSingleton<IStrategy, BasisArbitrageStrategy>();
        services.AddSingleton<IStrategy, SmaCrossoverStrategy>();
        services.AddSingleton<IStrategy, B46RsiBbStrategy>();
        services.AddSingleton<IStrategy, PriceActionPredictorStrategy>();
        services.AddSingleton<IStrategy, MtfMeanReversionStrategy>();
        services.AddSingleton<IStrategyFactory, StrategyFactory>();

        services.AddSingleton<IAccountSynchronizer, AccountSynchronizer>();

        // 通知服務：預設 NoOp。Infrastructure 層若偵測到有效 Discord/Telegram 設定會 Replace 此註冊。
        services.TryAddSingleton<INotificationService, NoOpNotificationService>();

        // S30 AI Advisor：預設 NoOp（回傳「未配置」提示）。Infrastructure 啟動時會 Replace 成
        // GeminiAiAdvisorService — 即便啟動時沒金鑰也會換上去，金鑰是 per-request 從 DB 讀，
        // 使用者在 UI 填入後立即生效，不必重啟。
        services.TryAddSingleton<IAiAdvisorService, NoOpAiAdvisorService>();

        // S30-ELITE+：AI 呼叫診斷 Ring Buffer（最後 20 筆）— UI /lab 與 /settings/exchanges
        // 透過 /api/ai/traces 讀取，讓使用者看到 Primary 404 / Fallback 安全過濾等真實原因。
        services.AddSingleton<IAiAdviceTraceLog, AiAdviceTraceLog>();

        // MarketContextBuilder 依賴 Singleton IExchangeClient，本身無狀態 — Singleton 即可。
        services.AddSingleton<IMarketContextBuilder, MarketContextBuilder>();

        // 即時推播：預設 NoOp。Web host（ConsoleApp）啟動時會 Replace 成 SignalR 版本。
        services.TryAddSingleton<IRealtimeBroadcaster, NullRealtimeBroadcaster>();

        // 環境熱切換編排器（S21）— Singleton，跨 process 內所有 UI / API 呼叫共用同一個 lock。
        // 內部依賴 IExchangeClient / IMarketDataStream / IStrategyRuntimeController，全部由 ConsoleApp host 提供。
        services.AddSingleton<IEnvironmentSwitcher, EnvironmentSwitcher>();

        return services;
    }
}
