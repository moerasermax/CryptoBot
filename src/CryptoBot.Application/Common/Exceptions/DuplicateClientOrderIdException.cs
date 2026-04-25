namespace CryptoBot.Application.Common.Exceptions;

/// <summary>
/// S66-A：本地 DB 或交易所端偵測到 ClientOrderId 唯一索引衝突時拋出。
///
/// 兩個來源：
///   * 本地 DB：UnitOfWork 攔截 <c>DbUpdateException</c>（SQLite error 19 / SQL Server 2627、2601）後轉拋。
///   * 交易所：BingX <c>PlaceOrderAsync</c> 嗅探到 <c>result.Error</c> 訊息含「duplicate clientOrderId」字樣。
///
/// 兩種來源都填入 <see cref="RawErrorCode"/> 與 <see cref="RawErrorMessage"/>（若有），
/// 讓 DiagnosticTool 的 probe 指令能列印出真實值，作為 <c>Institutional_Memory</c> 的探針資產。
///
/// 呼叫端的標準處置：
///   1. 視為「已知可回復狀態」— **嚴禁**呼叫 <c>_strategy.ReportError</c> 或停策略（IRON ⑤）。
///   2. 走通知 + 廣播雙軌（<c>[ORDER]</c> 前綴）讓 UI 看到。
///   3. 改呼叫 <c>IExchangeClient.GetOrderByClientOrderIdAsync</c> 對齊交易所端真實狀態。
/// </summary>
public sealed class DuplicateClientOrderIdException : Exception
{
    public string ClientOrderId { get; }

    /// <summary>交易所/DB 回傳的原始錯誤碼（BingX = 整數字串；SQLite = "19"）。null 代表來源未提供。</summary>
    public string? RawErrorCode { get; }

    /// <summary>交易所/DB 回傳的原始錯誤訊息（未經本地翻譯）。null 代表來源未提供。</summary>
    public string? RawErrorMessage { get; }

    public DuplicateClientOrderIdException(string clientOrderId)
        : this(clientOrderId, rawErrorCode: null, rawErrorMessage: null, innerException: null)
    {
    }

    public DuplicateClientOrderIdException(string clientOrderId, Exception innerException)
        : this(clientOrderId, rawErrorCode: null, rawErrorMessage: null, innerException)
    {
    }

    public DuplicateClientOrderIdException(
        string clientOrderId,
        string? rawErrorCode,
        string? rawErrorMessage,
        Exception? innerException = null)
        : base(BuildMessage(clientOrderId, rawErrorCode, rawErrorMessage), innerException)
    {
        ClientOrderId = clientOrderId;
        RawErrorCode = rawErrorCode;
        RawErrorMessage = rawErrorMessage;
    }

    private static string BuildMessage(string clientOrderId, string? code, string? msg)
    {
        if (code is null && msg is null)
            return $"ClientOrderId '{clientOrderId}' already exists.";
        return $"ClientOrderId '{clientOrderId}' already exists. RawCode={code ?? "(none)"}, RawMsg={msg ?? "(none)"}";
    }
}
