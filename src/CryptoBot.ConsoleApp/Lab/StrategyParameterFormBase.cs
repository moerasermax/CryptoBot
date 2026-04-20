using CryptoBot.ConsoleApp.Services;
using Microsoft.AspNetCore.Components;

namespace CryptoBot.ConsoleApp.Lab;

/// <summary>
/// 「策略大腦插槽」的合約：每個策略對應一個繼承這個的 Razor 元件，
/// 負責畫出自己的參數輸入框，並在被問到時把目前的輸入轉成 <see cref="OptimizationRequest"/>。
///
/// 元件之間透過 <see cref="ParameterChanged"/> EventCallback 把「目前格子數」回報給父頁面，
/// 父頁面就能顯示 grid size 預估，不必知道每個策略內部到底有幾個維度。
/// </summary>
public abstract class StrategyParameterFormBase : ComponentBase
{
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public EventCallback<int> ParameterChanged { get; set; }

    /// <summary>表單目前的參數總組合數（笛卡兒積展開後的格數）。</summary>
    public abstract int CurrentGridSize { get; }

    /// <summary>把目前表單狀態 + 時間窗組成優化請求；驗證失敗時回傳 null 並設 <paramref name="error"/>。</summary>
    public abstract OptimizationRequest? BuildRequest(DateTime startUtc, DateTime endUtc, out string? error);

    protected Task NotifyChangedAsync() => ParameterChanged.InvokeAsync(CurrentGridSize);
}
