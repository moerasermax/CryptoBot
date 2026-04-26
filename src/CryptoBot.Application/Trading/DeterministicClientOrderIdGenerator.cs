using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Application.Trading;

/// <summary>
/// S66-A：基於 SHA-256 的決定性 ClientOrderId 生成器。
///
/// 格式：<c>cb_{strategyIdPrefix:8}_{signalHash:8}</c>（總長 20 字，符合 BingX 32 字限制）
///   * strategyIdPrefix = StrategyId 的 GUID-N 形式前 8 hex char（保留 debug 可讀性，能直接看出
///     是哪個策略；不需反查 hash 表）。
///   * signalHash = SHA-256(canonical) 的前 8 hex char（32 bits ≈ 4.3 億命名空間，單一策略
///     終生不可能撞到）。
///
/// canonical 規則 — 用 <c>|</c> 分隔以避免歧義，所有欄位採固定字串表示，不依賴 culture：
/// <code>
///   {strategyId-N}|{symbol.BingXFormat}|{side}|{positionSide}|{signalCloseTimeUnixMs}
/// </code>
///
/// 抽换或重命名上面任一欄位都會改變 ID — 所以**禁止**未經團隊評估更動。
/// 變更會打破歷史訂單與當前生成器之間的對齊（雖然 DB 仍記原 ID，但 retry 場景會失靈）。
/// </summary>
public sealed class DeterministicClientOrderIdGenerator : IClientOrderIdGenerator
{
    private const string Prefix = "cb_";
    private const int StrategyPrefixLength = 8;
    private const int SignalHashLength = 8;

    public string Generate(
        Guid strategyId,
        Symbol symbol,
        OrderSide side,
        PositionSide positionSide,
        DateTime signalCloseTimeUtc)
    {
        var unixMs = new DateTimeOffset(signalCloseTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var canonical = string.Create(CultureInfo.InvariantCulture,
            $"{strategyId:N}|{symbol.BingXFormat}|{side}|{positionSide}|{unixMs}");

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical), hash);

        var stratPrefix = strategyId.ToString("N").AsSpan(0, StrategyPrefixLength);
        var sigHex = Convert.ToHexString(hash[..(SignalHashLength / 2)]).ToLowerInvariant();

        return string.Concat(Prefix, stratPrefix, "_", sigHex);
    }
}
