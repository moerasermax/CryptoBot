using System.Security.Cryptography;
using System.Text;

namespace CryptoBot.ConsoleApp.Lab;

/// <summary>
/// S25 — Lab 快取存檔用的 StrategyKey → Guid 對映。
///
/// 原因：
/// <see cref="Domain.Aggregates.StrategyOptimizationAggregate.StrategyOptimizationSettings"/> 的複合鍵首鍵是
/// <c>Guid StrategyId</c>，設計時綁到 Strategy Aggregate 的主鍵。但 <c>/lab</c> 的優化掃描是「探索」
/// 行為 — 當下不一定掛著某個正在跑的 Strategy，我們只認策略類別（"sma" / "trend" / …）。
///
/// 所以這裡做 <c>MD5("lab-strategy:" + key)</c> 的確定性雜湊 → Guid。
/// 相同 key 永遠產生同一個 Guid，快取讀寫對齊；不同策略之間互不干擾。
///
/// 不用 GUID v5：.NET BCL 沒內建 v5 實作；此處純粹是 Lab 內部索引，語意等價。
/// </summary>
public static class LabStrategyKey
{
    public static Guid ToGuid(string strategyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyKey);
        var bytes = Encoding.UTF8.GetBytes("lab-strategy:" + strategyKey);
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }
}
