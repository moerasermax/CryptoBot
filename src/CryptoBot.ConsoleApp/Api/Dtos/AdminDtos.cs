namespace CryptoBot.ConsoleApp.Api.Dtos;

/// <summary>S57 T1：/admin 頁面「目前請求者的真實 IP」— 由 UseForwardedHeaders 改寫過。</summary>
public sealed record CallerIpDto(string Ip, bool InWhitelist);

/// <summary>S57 T1：白名單列表回傳。</summary>
public sealed record WhitelistDto(IReadOnlyList<string> AllowedIps);

/// <summary>S57 T1：新增 IP 請求 body。</summary>
public sealed record AddWhitelistRequest(string Ip);

/// <summary>S57 T1：白名單變更回應 — UI 用 Outcome 決定 toast 文案。</summary>
public sealed record WhitelistMutationResponse(string Outcome, int TotalCount);

/// <summary>
/// S57 T2：深度交易回溯的一筆。<c>Strategy</c> 與 <c>Snapshot</c> 分兩組鍵值 —
/// Strategy 是「開倉時鎖定的模型名」，Snapshot 是「當下的網格參數」解析後的 kv 清單。
/// </summary>
public sealed record DeepPositionDto(
    Guid Id,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal EntryPrice,
    decimal? ExitPrice,
    decimal RealizedPnL,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc,
    string? StrategyType,
    IReadOnlyList<SnapshotKvDto> Snapshot);

/// <summary>S57 T2：ParametersSnapshot 解析出的單一 kv pair。Value 已格式化成可讀字串。</summary>
public sealed record SnapshotKvDto(string Key, string Value);

/// <summary>
/// S57 T3：資料庫健康狀態。SizeBytes 含主檔 + WAL + SHM。
/// PositionCount 是歷史持倉筆數，OrderCount 涵蓋開平倉所有訂單。
/// </summary>
public sealed record DbHealthDto(
    string DataSourcePath,
    long MainFileBytes,
    long TotalBytes,
    int PositionCount,
    int OrderCount,
    int StrategyCount,
    int LogFileCount,
    long LogTotalBytes);

/// <summary>S57 T3：VACUUM 執行結果。</summary>
public sealed record DbVacuumResponse(
    long BytesBefore,
    long BytesAfter,
    long BytesReclaimed,
    int ElapsedMs);
