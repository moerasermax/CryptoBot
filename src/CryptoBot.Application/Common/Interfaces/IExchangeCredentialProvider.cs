using CryptoBot.Domain.Enums;

namespace CryptoBot.Application.Common.Interfaces;

/// <summary>
/// 活躍交易所金鑰快照。若該交易所沒有任何 active 帳號，
/// <see cref="IsConfigured"/> = false、<see cref="ApiKey"/> / <see cref="ApiSecret"/> 為空字串。
/// </summary>
public sealed record ExchangeCredentials(
    ExchangeName Exchange,
    string AccountName,
    string ApiKey,
    string ApiSecret,
    bool IsConfigured);

/// <summary>
/// 提供當前啟用的交易所金鑰。S24 引入，用來取代直接讀 appsettings.json。
///
/// 責任：
/// 1. 對指定交易所讀出該交易所唯一 active 帳號的金鑰（沒有就回 IsConfigured=false）。
/// 2. UI 儲存新帳號或切換 active 後呼叫 <see cref="NotifyCredentialsChangedAsync"/>，
///    讓註冊的 <see cref="CredentialsChanged"/> 事件訂閱者（如 BingXExchangeClient）重建 SDK client。
/// </summary>
public interface IExchangeCredentialProvider
{
    /// <summary>
    /// 取出指定交易所當前啟用金鑰。若無 active 帳號或金鑰空白，回傳 IsConfigured=false 的占位物件。
    /// </summary>
    Task<ExchangeCredentials> GetActiveAsync(ExchangeName exchange, CancellationToken ct = default);

    /// <summary>
    /// UI 儲存/切換 active 帳號後由 API 端點呼叫，觸發 <see cref="CredentialsChanged"/>。
    /// 實作必須保證事件在呼叫完成前同步推送完畢（或 await 到所有 handler 結束）。
    /// </summary>
    Task NotifyCredentialsChangedAsync(ExchangeName exchange, CancellationToken ct = default);

    /// <summary>
    /// 當某交易所 active 金鑰被更新時觸發。訂閱者應以 fire-and-forget 或自行包 Task 避免阻塞。
    /// </summary>
    event EventHandler<ExchangeCredentialsChangedEventArgs>? CredentialsChanged;
}

public sealed class ExchangeCredentialsChangedEventArgs : EventArgs
{
    public ExchangeName Exchange { get; }
    public ExchangeCredentials Credentials { get; }

    public ExchangeCredentialsChangedEventArgs(ExchangeName exchange, ExchangeCredentials credentials)
    {
        Exchange = exchange;
        Credentials = credentials;
    }
}
