namespace CryptoBot.ConsoleApp.Lab;

/// <summary>
/// 「策略大腦」目錄 — 把每一個可選的決策模型描述成一筆 <see cref="StrategyModel"/>，
/// 並指向它的 Blazor 參數表單元件型別。
///
/// 加新策略 = 三個動作：
///   1) 寫一個 <c>FooParameterForm.razor</c> 繼承 <see cref="StrategyParameterFormBase"/>
///   2) 在這個 catalog 的 ctor 裡 <c>Register(new StrategyModel(...))</c>
///   3) 在 <c>OptimizationOrchestrator.ResolveStrategy</c> + <c>IsValidCombination</c> +
///      <c>FormatSummary</c> 加對應 key 的 case
/// </summary>
public sealed class StrategyCatalog
{
    private readonly List<StrategyModel> _models = new();

    public StrategyCatalog()
    {
        Register(new StrategyModel(
            Key: "sma",
            DisplayName: "SMA Crossover",
            Subtitle: "雙均線金叉 / 死叉",
            FormComponent: typeof(Components.Lab.SmaParameterForm),
            IsLocked: false,
            ExpectedParameterKeys: new[] { "FastSmaPeriod", "SlowSmaPeriod" }));

        // S32-S35-REVISED T1：業界術語大一統 — DisplayName 改為 `<指標>` + `<形式>` 一眼能看懂的英文命名。
        // 同名字也會透過 AiAdvisorEndpoints 傳進 Gemini Prompt (`{req.StrategyDisplayName}`)，Prompt 自動對齊。
        Register(new StrategyModel(
            Key: "rsi-bb",
            DisplayName: "B46 Hybrid Model",
            Subtitle: "RSI 超買超賣 × Bollinger 通道反轉 複合訊號",
            FormComponent: typeof(Components.Lab.B46ParameterForm),
            IsLocked: false,
            ExpectedParameterKeys: new[] { "RsiPeriod", "RsiOversold", "RsiOverbought", "BbPeriod", "BbStdDev" }));

        Register(new StrategyModel(
            Key: "trend",
            DisplayName: "EMA Trend Following",
            Subtitle: "EMA 黃金/死亡交叉 × RSI 動能確認",
            FormComponent: typeof(Components.Lab.TrendFollowingParameterForm),
            IsLocked: false,
            ExpectedParameterKeys: new[] { "FastEmaPeriod", "SlowEmaPeriod", "RsiPeriod", "RsiMidline" }));

        Register(new StrategyModel(
            Key: "mean-reversion",
            DisplayName: "Bollinger Reversion",
            Subtitle: "Bollinger 觸軌 × RSI 極端反轉",
            FormComponent: typeof(Components.Lab.MeanReversionParameterForm),
            IsLocked: false,
            ExpectedParameterKeys: new[] { "BbPeriod", "BbStdDev", "RsiPeriod", "RsiOversold", "RsiOverbought" }));

        // S43：Price Action Predictor — 裸 K 形態 + 動能。不依賴任何移動平均 / 震盪指標，
        // 純從 OHLC 結構推斷方向 + 主動反轉出場。
        Register(new StrategyModel(
            Key: "pa",
            DisplayName: "Price Action Predictor",
            Subtitle: "裸 K 形態 × 動能雙驗證，反向形態主動平倉",
            FormComponent: typeof(Components.Lab.PaParameterForm),
            IsLocked: false,
            ExpectedParameterKeys: new[] { "LookbackPeriod", "MomentumThreshold", "WickToBodyRatio", "EngulfingEnabled", "Confidence" }));
    }

    public IReadOnlyList<StrategyModel> Models => _models;
    public StrategyModel Default => _models.First(m => !m.IsLocked);
    public StrategyModel? FindByKey(string key) => _models.FirstOrDefault(m => m.Key == key);

    private void Register(StrategyModel model) => _models.Add(model);
}

public sealed record StrategyModel(
    string Key,
    string DisplayName,
    string Subtitle,
    Type? FormComponent,
    bool IsLocked,
    IReadOnlyList<string> ExpectedParameterKeys);
