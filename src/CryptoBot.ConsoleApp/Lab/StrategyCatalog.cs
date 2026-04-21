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
            IsLocked: false));

        Register(new StrategyModel(
            Key: "rsi-bb",
            DisplayName: "B46 · RSI + Bollinger",
            Subtitle: "通道反轉 × 超買超賣",
            FormComponent: typeof(Components.Lab.B46ParameterForm),
            IsLocked: false));
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
    bool IsLocked);
