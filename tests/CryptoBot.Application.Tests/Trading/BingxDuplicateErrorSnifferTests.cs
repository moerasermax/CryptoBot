using CryptoBot.Infrastructure.Exchange.BingX;
using Xunit;

namespace CryptoBot.Application.Tests.Trading;

/// <summary>
/// S66-A T0：鎖死 BingX duplicate clientOrderId 偵測契約（errorCode + message 雙保險）。
///
/// **2026-04-25 探針確診值**：
///   - errorCode = <c>101400</c>
///   - message   = <c>"clientOrderID unique check failed"</c>
///
/// 詳見 <c>management/protocols/Institutional_Memory.md §S66-A</c>。
///
/// 任何修改 <see cref="BingXExchangeClient.IsDuplicateClientOrderIdError"/> 的人都必須：
///   1. 保留第一條 <c>[InlineData(101400, "clientOrderID unique check failed")]</c>，
///   2. 若 BingX 升 SDK 改了 code 或 message，**先重跑 probe-bingx 取得新值**再來改測試。
/// </summary>
public class BingxDuplicateErrorSnifferTests
{
    private const int RealCode = 101400;
    private const string RealMessage = "clientOrderID unique check failed";

    [Theory]
    // ===== T0 確診組合 =====
    [InlineData(RealCode, RealMessage)]                   // ← 真實 T0 探針：code + message 雙命中
    // ===== code 主防線（即便 message 為空也應命中）=====
    [InlineData(RealCode, "")]
    [InlineData(RealCode, null)]
    [InlineData(RealCode, "完全不相關的訊息")]            // code 對就視為命中，不需要 message 配合
    // ===== message 備援防線（code 缺失或不同時，靠訊息嗅探兜）=====
    [InlineData(null, "clientOrderID unique check failed")]
    [InlineData(null, "ClientOrderId unique check failed")]   // 大小寫變體
    [InlineData(null, "client order id duplicate")]           // 防禦詞
    [InlineData(null, "clientOrderId already exists")]        // 防禦詞
    [InlineData(null, "ClientOrderID exists in the system")]  // 防禦詞
    [InlineData(99999, "clientOrderID unique check failed")]  // code 不對但 message 對 → 訊息備援命中
    public void Recognises_known_duplicate_signals(int? errorCode, string? message)
    {
        Assert.True(BingXExchangeClient.IsDuplicateClientOrderIdError(errorCode, message),
            $"應視為 duplicate 但被嗅探漏掉：code={errorCode}, msg={message}");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData(null, "Insufficient balance")]                          // 餘額不足
    [InlineData(101204, "Insufficient balance")]                        // 不同 code、不同類別
    [InlineData(null, "Symbol not found")]
    [InlineData(null, "Price below minimum")]
    [InlineData(null, "Position would exceed maximum leverage")]
    [InlineData(null, "client connection lost")]                        // 含 client 但非 duplicate（缺第二關鍵字）
    [InlineData(99999, "")]                                             // 隨機 code、空訊息
    public void Does_not_misidentify_unrelated_errors(int? errorCode, string? message)
    {
        Assert.False(BingXExchangeClient.IsDuplicateClientOrderIdError(errorCode, message),
            $"不該被當成 duplicate：code={errorCode}, msg={message}");
    }

    /// <summary>
    /// 鎖死 errorCode 常數本身。任何人改動 <see cref="BingXExchangeClient.BingxDuplicateClientOrderIdErrorCode"/>
    /// 必須先重跑 <c>probe-bingx</c> 確認新值。
    /// </summary>
    [Fact]
    public void T0_constant_matches_2026_04_25_probe_evidence()
    {
        Assert.Equal(101400, BingXExchangeClient.BingxDuplicateClientOrderIdErrorCode);
    }
}
