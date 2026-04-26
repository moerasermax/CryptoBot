using CryptoBot.Domain.Aggregates.ExchangeAccountAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Exceptions;
using Xunit;

namespace CryptoBot.Domain.Tests;

/// <summary>
/// S24 — ExchangeAccount Aggregate 的核心不變式測試。
/// 只測 Domain 行為（Create 驗證、UpdateCredentials 空字串保留原值、Activate 拒絕空金鑰）。
/// 「同交易所至多一筆 active」的業務規則由 Repository.SetActiveAsync 保證，不在此層。
/// </summary>
public class ExchangeAccountTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsInstanceWithGuid()
    {
        var a = ExchangeAccount.Create(
            ExchangeName.BingX, "main", "key-123", "secret-xyz", isActive: false);

        Assert.NotEqual(Guid.Empty, a.Id);
        Assert.Equal(ExchangeName.BingX, a.Exchange);
        Assert.Equal("main", a.AccountName);
        Assert.Equal("key-123", a.ApiKey);
        Assert.Equal("secret-xyz", a.ApiSecret);
        Assert.False(a.IsActive);
        Assert.True(a.HasCredentials);
        Assert.True(a.CreatedAt <= DateTime.UtcNow);
        Assert.Equal(a.CreatedAt, a.UpdatedAt);
    }

    [Fact]
    public void Create_TrimsAccountName()
    {
        var a = ExchangeAccount.Create(ExchangeName.BingX, "  main  ", "k", "s");
        Assert.Equal("main", a.AccountName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankAccountName_Throws(string name)
    {
        Assert.Throws<DomainException>(() =>
            ExchangeAccount.Create(ExchangeName.BingX, name, "k", "s"));
    }

    [Fact]
    public void Create_AccountNameTooLong_Throws()
    {
        var tooLong = new string('x', 65);
        Assert.Throws<DomainException>(() =>
            ExchangeAccount.Create(ExchangeName.BingX, tooLong, "k", "s"));
    }

    [Fact]
    public void Create_NullKeysBecomeEmptyStrings()
    {
        var a = ExchangeAccount.Create(ExchangeName.BingX, "main", null!, null!);
        Assert.Equal(string.Empty, a.ApiKey);
        Assert.Equal(string.Empty, a.ApiSecret);
        Assert.False(a.HasCredentials);
    }

    [Fact]
    public void UpdateCredentials_EmptyValues_PreserveExisting()
    {
        // UI 預設把 Secret 顯示成遮罩；若使用者只改 key，送空 secret 回來必須保留原值。
        var a = ExchangeAccount.Create(ExchangeName.BingX, "main", "old-key", "old-secret");

        a.UpdateCredentials(newAccountName: null, newApiKey: string.Empty, newApiSecret: string.Empty);

        Assert.Equal("old-key", a.ApiKey);
        Assert.Equal("old-secret", a.ApiSecret);
    }

    [Fact]
    public void UpdateCredentials_OnlyProvidedFieldsChange()
    {
        var a = ExchangeAccount.Create(ExchangeName.BingX, "main", "old-key", "old-secret");
        var originalUpdatedAt = a.UpdatedAt;
        System.Threading.Thread.Sleep(2); // 確保時間戳有變

        a.UpdateCredentials(newAccountName: "renamed", newApiKey: "new-key", newApiSecret: null);

        Assert.Equal("renamed", a.AccountName);
        Assert.Equal("new-key", a.ApiKey);
        Assert.Equal("old-secret", a.ApiSecret);
        Assert.True(a.UpdatedAt > originalUpdatedAt);
    }

    [Fact]
    public void UpdateCredentials_NameTooLong_Throws()
    {
        var a = ExchangeAccount.Create(ExchangeName.BingX, "main", "k", "s");
        var tooLong = new string('x', 65);

        Assert.Throws<DomainException>(() =>
            a.UpdateCredentials(tooLong, null, null));
    }

    [Fact]
    public void Activate_WithValidCredentials_SetsIsActive()
    {
        var a = ExchangeAccount.Create(ExchangeName.BingX, "main", "k", "s");

        a.Activate();

        Assert.True(a.IsActive);
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("   ", "secret")]
    [InlineData("key", "")]
    [InlineData("key", "   ")]
    public void Activate_WithBlankCredentials_Throws(string apiKey, string apiSecret)
    {
        var a = ExchangeAccount.Create(ExchangeName.BingX, "main", apiKey, apiSecret);
        Assert.Throws<DomainException>(() => a.Activate());
        Assert.False(a.IsActive);
    }

    [Fact]
    public void Deactivate_WhenActive_SetsInactive()
    {
        var a = ExchangeAccount.Create(ExchangeName.BingX, "main", "k", "s", isActive: true);

        a.Deactivate();

        Assert.False(a.IsActive);
    }

    [Fact]
    public void Deactivate_IsIdempotent()
    {
        var a = ExchangeAccount.Create(ExchangeName.BingX, "main", "k", "s");

        a.Deactivate();
        a.Deactivate();

        Assert.False(a.IsActive);
    }

    [Fact]
    public void HasCredentials_RequiresBothKeys()
    {
        Assert.False(ExchangeAccount.Create(ExchangeName.BingX, "main", "", "").HasCredentials);
        Assert.False(ExchangeAccount.Create(ExchangeName.BingX, "main", "k", "").HasCredentials);
        Assert.False(ExchangeAccount.Create(ExchangeName.BingX, "main", "", "s").HasCredentials);
        Assert.True(ExchangeAccount.Create(ExchangeName.BingX, "main", "k", "s").HasCredentials);
    }
}
