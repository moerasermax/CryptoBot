namespace CryptoBot.Domain.Exceptions;

/// <summary>
/// 領域層異常 - 當違反領域規則或不變式時拋出
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 風險管理異常 - 當違反風險規則時拋出
/// </summary>
public class RiskManagementException : DomainException
{
    public RiskManagementException(string message) : base(message) { }
}

/// <summary>
/// 不足的保證金異常
/// </summary>
public class InsufficientMarginException : DomainException
{
    public decimal Required { get; }
    public decimal Available { get; }

    public InsufficientMarginException(decimal required, decimal available)
        : base($"Insufficient margin. Required: {required}, Available: {available}")
    {
        Required = required;
        Available = available;
    }
}
