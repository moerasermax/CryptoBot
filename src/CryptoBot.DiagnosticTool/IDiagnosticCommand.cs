namespace CryptoBot.DiagnosticTool;

/// <summary>
/// S59-ADD T1：每個診斷子命令實作此介面，DI 會自動發現並註冊。
///
/// 新增一支命令只需寫一個 class 實作本介面並註冊成 <c>IDiagnosticCommand</c> 到容器，
/// <see cref="Program"/> 的 dispatcher 透過 <c>IEnumerable&lt;IDiagnosticCommand&gt;</c>
/// 取得全部實例後依 Name / Aliases 派發 — 進入點不必動到。
/// </summary>
public interface IDiagnosticCommand
{
    /// <summary>主要命令名，保留小寫（dispatcher 會自動 ToLowerInvariant 比對）。</summary>
    string Name { get; }

    /// <summary>可選別名；例如 <c>env</c> 與 <c>environment</c> 都接受。</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>一行描述 — 會顯示在 help 輸出裡。</summary>
    string Description { get; }

    /// <summary>Usage 範例字串 — 會顯示在 help 輸出裡。</summary>
    string Usage { get; }

    /// <summary>
    /// 執行命令。<paramref name="args"/> 已經去掉前面的命令名本身。
    /// 回傳 process exit code（0=成功，其他值代表失敗）。
    /// </summary>
    Task<int> RunAsync(string[] args, CancellationToken ct);
}
