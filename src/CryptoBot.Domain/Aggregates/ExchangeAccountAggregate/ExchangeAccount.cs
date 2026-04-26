using CryptoBot.Domain.Common;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Exceptions;

namespace CryptoBot.Domain.Aggregates.ExchangeAccountAggregate;

/// <summary>
/// 交易所帳號 Aggregate Root — 把 API 金鑰從 appsettings.json 搬進 SQLite，
/// 支援 UI 即時新增/切換、不必重啟。S24 引入。
///
/// 不變式：
/// - 同一時刻最多一筆 IsActive = true（由 Repository 在切換時保證）。
/// - ApiKey / ApiSecret 至少要有一筆才能 Activate（空字串視為「未配置」）。
///
/// 安全提醒：金鑰在 SQLite 以明文存放。若 cryptobot.db 落入第三人，金鑰即洩漏。
/// 短期 mitigation = 仰賴 Windows 檔案權限；中期應掛 Data Protection API 加密。
/// </summary>
public sealed class ExchangeAccount : AggregateRoot<Guid>
{
    public ExchangeName Exchange { get; private set; }
    public string AccountName { get; private set; }
    public string ApiKey { get; private set; }
    public string ApiSecret { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private ExchangeAccount() : base(Guid.NewGuid())
    {
        AccountName = default!;
        ApiKey = default!;
        ApiSecret = default!;
    }

    public static ExchangeAccount Create(
        ExchangeName exchange,
        string accountName,
        string apiKey,
        string apiSecret,
        bool isActive = false)
    {
        if (string.IsNullOrWhiteSpace(accountName))
            throw new DomainException("Account name cannot be empty.");
        if (accountName.Length > 64)
            throw new DomainException("Account name must be ≤ 64 chars.");

        var now = DateTime.UtcNow;
        return new ExchangeAccount
        {
            Exchange = exchange,
            AccountName = accountName.Trim(),
            ApiKey = apiKey ?? string.Empty,
            ApiSecret = apiSecret ?? string.Empty,
            IsActive = isActive,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// 更新金鑰與顯示名稱。空字串會被當作「保留原值」處理，避免 UI 誤以遮罩字串覆寫真金鑰。
    /// </summary>
    public void UpdateCredentials(string? newAccountName, string? newApiKey, string? newApiSecret)
    {
        if (!string.IsNullOrWhiteSpace(newAccountName))
        {
            if (newAccountName.Length > 64)
                throw new DomainException("Account name must be ≤ 64 chars.");
            AccountName = newAccountName.Trim();
        }

        if (!string.IsNullOrEmpty(newApiKey))
            ApiKey = newApiKey;

        if (!string.IsNullOrEmpty(newApiSecret))
            ApiSecret = newApiSecret;

        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(ApiSecret))
            throw new DomainException("Cannot activate an account with empty ApiKey / ApiSecret.");
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
}
