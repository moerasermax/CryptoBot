using CryptoBot.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CryptoBot.Infrastructure.Persistence.ValueConverters;

/// <summary>
/// Symbol 值物件 ↔ 單一字串的 EF Core 轉換器。
///
/// 寫入：Symbol → "BTC-USDT"（透過 <see cref="Symbol.BingXFormat"/>）
/// 讀取："BTC-USDT" → Symbol（透過 <see cref="Symbol.Parse"/>，亦相容 "BTCUSDT"）
///
/// 為什麼是字串而非 owned-type / 兩欄拆分：
///   1. NextWork.md 明確要求單一字串欄位
///   2. 跨 DB（Mongo / Redis）切換時，原始欄位語意一致
///   3. 索引 / 查詢條件直觀（WHERE Symbol = 'BTC-USDT'）
/// </summary>
public sealed class SymbolConverter : ValueConverter<Symbol, string>
{
    public SymbolConverter()
        : base(
            v => v.BingXFormat,
            v => Symbol.Parse(v))
    {
    }
}
