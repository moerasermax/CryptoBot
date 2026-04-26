using CryptoBot.Domain.Aggregates.ExchangeAccountAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Exceptions;
using Xunit;

namespace CryptoBot.Domain.Tests;

/// <summary>
/// S22 T1b — 金鑰管理健壯化所需的 <see cref="ExchangeAccount"/> 不變式回歸測試。
///
/// 定位：這裡不重複已存在的 S24 <c>ExchangeAccountTests</c>；重點是用 S22 分類標記把
/// 本任務直接依賴的兩條規則「鎖在 <c>dotnet test --filter Category=S22</c> 的網內」，
/// 任何未來對 ExchangeAccount 的重構一旦放寬這兩條規則，S22 自動紅燈。
///
/// 覆蓋規則（對應 TASK_S22_CORE_DEV.md v2.0 T1）：
/// - Activate 空金鑰被拒 — 搭配 Infrastructure 端移除 appsettings fallback 後，
///   Domain 必須保證「沒金鑰就不能 active」，避免空字串被誤標為 active 造成啟動期無聲失敗。
/// - UpdateCredentials 空字串保留原值 — UI 以遮罩字串顯示 Secret，使用者未觸碰該欄時
///   送回空字串不得覆寫真金鑰，否則一次編輯就會把正式金鑰抹成空。
/// </summary>
[Trait("Category", "S22")]
public class ExchangeAccountS22Tests
{
    [Theory]
    [InlineData("", "secret")]
    [InlineData("   ", "secret")]
    [InlineData("key", "")]
    [InlineData("key", "   ")]
    [InlineData("", "")]
    public void Activate_WithBlankApiKeyOrSecret_Throws(string apiKey, string apiSecret)
    {
        var a = ExchangeAccount.Create(ExchangeName.BingX, "main", apiKey, apiSecret);

        Assert.Throws<DomainException>(() => a.Activate());
        Assert.False(a.IsActive);
    }

    [Fact]
    public void UpdateCredentials_WithEmptyStrings_PreservesOriginalValues()
    {
        var a = ExchangeAccount.Create(ExchangeName.BingX, "main", "real-key", "real-secret");

        a.UpdateCredentials(newAccountName: null, newApiKey: string.Empty, newApiSecret: string.Empty);

        Assert.Equal("real-key", a.ApiKey);
        Assert.Equal("real-secret", a.ApiSecret);
    }

    [Fact]
    public void UpdateCredentials_WithNulls_PreservesOriginalValues()
    {
        var a = ExchangeAccount.Create(ExchangeName.BingX, "main", "real-key", "real-secret");

        a.UpdateCredentials(newAccountName: null, newApiKey: null, newApiSecret: null);

        Assert.Equal("real-key", a.ApiKey);
        Assert.Equal("real-secret", a.ApiSecret);
    }

    [Fact]
    public void UpdateCredentials_OnlySecretChanges_KeyIsPreserved()
    {
        var a = ExchangeAccount.Create(ExchangeName.BingX, "main", "real-key", "old-secret");

        a.UpdateCredentials(newAccountName: null, newApiKey: string.Empty, newApiSecret: "new-secret");

        Assert.Equal("real-key", a.ApiKey);
        Assert.Equal("new-secret", a.ApiSecret);
    }
}
