using CryptoBot.Application.Ai;

namespace CryptoBot.ConsoleApp.Lab;

/// <summary>
/// S74-C：把 ConsoleApp 層的 <see cref="StrategyCatalog"/> adapter 為 Application 層介面
/// <see cref="IStrategyParameterKeyCatalog"/>，讓 Infrastructure 的 <c>GlobalAiChatService</c>
/// 可拿到 union parameter keys 餵 <c>AiAdvicePayloadParser</c>，
/// 同時維持 IRON ⑥ 四層相依（Infrastructure 不知 ConsoleApp）。
/// </summary>
internal sealed class StrategyParameterKeyCatalogAdapter : IStrategyParameterKeyCatalog
{
    private readonly StrategyCatalog _catalog;
    private readonly Lazy<IReadOnlyList<string>> _allKeys;

    public StrategyParameterKeyCatalogAdapter(StrategyCatalog catalog)
    {
        _catalog = catalog;
        _allKeys = new Lazy<IReadOnlyList<string>>(() =>
            _catalog.Models
                .SelectMany(m => m.ExpectedParameterKeys)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    public IReadOnlyList<string> AllParameterKeys => _allKeys.Value;

    public IReadOnlyList<string>? KeysFor(string strategyKey) =>
        _catalog.FindByKey(strategyKey)?.ExpectedParameterKeys;
}
