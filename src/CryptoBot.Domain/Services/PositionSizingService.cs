using CryptoBot.Domain.Exceptions;
using CryptoBot.Domain.ValueObjects;

namespace CryptoBot.Domain.Services;

/// <summary>
/// 倉位大小計算 Domain Service
/// 
/// 核心風險管理原則：
/// 1. 凱利公式的簡化版：每筆風險不超過總資金的固定比例
/// 2. 位置大小 = (總資金 * 風險% ) / (入場價 - 止損價)
/// 
/// 這是純領域邏輯，不依賴任何外部基礎設施。
/// </summary>
public static class PositionSizingService
{
    /// <summary>
    /// 根據固定風險計算倉位大小 (幣本位數量)
    /// </summary>
    /// <param name="accountBalance">帳戶總資金 (USDT)</param>
    /// <param name="riskPercent">單筆風險 % (例如 0.02 = 2%)</param>
    /// <param name="entryPrice">計畫進場價</param>
    /// <param name="stopLossPrice">止損價</param>
    /// <param name="leverage">槓桿倍數</param>
    /// <param name="minQuantity">最小下單量 (交易所規定)</param>
    /// <param name="stepSize">數量精度 (例如 0.001)</param>
    /// <returns>應下單的基礎資產數量</returns>
    public static Quantity CalculatePositionSize(
        decimal accountBalance,
        decimal riskPercent,
        Price entryPrice,
        Price stopLossPrice,
        Leverage leverage,
        decimal minQuantity = 0.001m,
        decimal stepSize = 0.001m)
    {
        if (accountBalance <= 0)
            throw new DomainException("Account balance must be positive.");
        if (riskPercent <= 0 || riskPercent > 0.5m)
            throw new DomainException(
                $"Risk percent must be in (0, 50%]: {riskPercent:P}");

        var priceDiff = Math.Abs(entryPrice.Value - stopLossPrice.Value);
        if (priceDiff <= 0)
            throw new DomainException("Entry and stop loss cannot be equal.");

        // 風險金額 = 帳戶餘額 * 風險百分比
        var riskAmount = accountBalance * riskPercent;

        // 基礎數量 = 風險金額 / 每單位價差
        var rawQuantity = riskAmount / priceDiff;

        // 檢查：所需保證金不得超過帳戶可用
        var notionalValue = entryPrice.Value * rawQuantity;
        var requiredMargin = notionalValue / leverage.Value;
        if (requiredMargin > accountBalance)
        {
            // 保證金不足，縮減至最大可用
            rawQuantity = accountBalance * leverage.Value / entryPrice.Value;
        }

        // 對齊 stepSize
        var alignedQuantity = Math.Floor(rawQuantity / stepSize) * stepSize;

        // 檢查最小下單量
        if (alignedQuantity < minQuantity)
            throw new InsufficientMarginException(
                required: minQuantity * entryPrice.Value / leverage.Value,
                available: accountBalance);

        return Quantity.Create(alignedQuantity);
    }

    /// <summary>
    /// 依信號強度 (0~1) 調整倉位大小
    /// </summary>
    public static Quantity AdjustByConfidence(Quantity baseQuantity, decimal confidence)
    {
        confidence = Math.Clamp(confidence, 0m, 1m);
        return Quantity.Create(baseQuantity.Value * confidence);
    }
}
