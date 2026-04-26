using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.Repositories;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBot.DiagnosticTool.Commands;

/// <summary>
/// S66-A：依 ClientOrderId 比對本地 Orders 表 vs 交易所端真實狀態，驗證冪等鏈路。
///
/// 三類輸出：
///   - <b>本地有、交易所無</b>：本地已寫入但 <c>PlaceOrderAsync</c> 尚未成功或已失敗 —
///     冪等保護正常工作（clientOrderId 不會在交易所被衍生為新訂單），但需檢查為何
///     交易所從未收到此訂單。
///   - <b>本地無、交易所有</b>：交易所有此 clientOrderId 的紀錄但本地 DB 空 — 罕見；
///     可能是其他來源用了同字串，或本地 DB 被清過。
///   - <b>雙方都有但狀態不同</b>：本地值過時，建議走 <c>RefreshOrderStatusAsync</c> 對齊。
///
/// 指令純讀，不自動修復（避免診斷工具誤操作下單 / 撤單）。
/// </summary>
public sealed class CheckOrderCommand : IDiagnosticCommand
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CheckOrderCommand(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string Name => "s66a_check-order";
    public IReadOnlyList<string> Aliases => new[] { "check-order" };
    public string Description => "Compare local Orders row vs exchange state by ClientOrderId.";
    public string Usage => "s66a_check-order <ClientOrderId> [Symbol]";

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine($"Usage: {Usage}");
            Console.Error.WriteLine("  ClientOrderId 為決定性 ID（S66-A 預設格式 cb_xxxxxxxx_xxxxxxxx）");
            Console.Error.WriteLine("  Symbol 可選 — 預設從本地 Orders 讀到的 row 拿 Symbol；若本地查無則必填。");
            return 2;
        }

        var clientOrderId = args[0].Trim();
        if (string.IsNullOrEmpty(clientOrderId))
        {
            Console.Error.WriteLine("ClientOrderId must not be empty.");
            return 2;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var orderRepo = sp.GetRequiredService<IOrderRepository>();
        var exchange = sp.GetRequiredService<IExchangeClient>();

        Console.WriteLine("=== S66-A ClientOrderId Inspector ===");
        Console.WriteLine($"ClientOrderId : {clientOrderId}");
        Console.WriteLine($"Mode          : {exchange.CurrentMode}");
        Console.WriteLine();

        // 1) 本地查
        var local = await orderRepo.GetByClientOrderIdAsync(clientOrderId, ct).ConfigureAwait(false);

        Symbol? symbol = null;
        if (args.Length >= 2)
        {
            try { symbol = Symbol.Parse(args[1]); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Invalid symbol '{args[1]}': {ex.Message}");
                return 2;
            }
        }
        else if (local is not null)
        {
            symbol = local.Symbol;
        }

        if (symbol is null)
        {
            Console.Error.WriteLine("本地查無此 ClientOrderId，必須在第二個參數指定 Symbol 才能查交易所。");
            Console.WriteLine();
            Console.WriteLine("Local  : not found");
            Console.WriteLine("Remote : (skipped — symbol unknown)");
            return 3;
        }

        Console.WriteLine("-- Local --");
        if (local is null)
        {
            Console.WriteLine("  (not found in Orders table)");
        }
        else
        {
            Console.WriteLine($"  Id              : {local.Id}");
            Console.WriteLine($"  Symbol          : {local.Symbol.BingXFormat}");
            Console.WriteLine($"  Side / PosSide  : {local.Side} / {local.PositionSide}");
            Console.WriteLine($"  Status          : {local.Status}");
            Console.WriteLine($"  Quantity        : {local.FilledQuantity.Value}/{local.Quantity.Value}");
            Console.WriteLine($"  AvgFillPrice    : {local.AverageFillPrice?.Value.ToString() ?? "(none)"}");
            Console.WriteLine($"  ExchangeOrderId : {local.ExchangeOrderId ?? "(none)"}");
            Console.WriteLine($"  CreatedAt       : {local.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        }
        Console.WriteLine();

        // 2) 交易所查
        Console.WriteLine("-- Exchange --");
        ExchangeOrderSnapshot? remote;
        try
        {
            remote = await exchange.GetOrderByClientOrderIdAsync(symbol, clientOrderId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] GetOrderByClientOrderIdAsync failed: {ex.Message}");
            return 5;
        }

        if (remote is null)
        {
            Console.WriteLine("  (not found on exchange)");
        }
        else
        {
            Console.WriteLine($"  ExchangeOrderId : {remote.ExchangeOrderId}");
            Console.WriteLine($"  Symbol          : {remote.Symbol.BingXFormat}");
            Console.WriteLine($"  Side / PosSide  : {remote.Side} / {remote.PositionSide}");
            Console.WriteLine($"  Status          : {remote.Status}");
            Console.WriteLine($"  Quantity        : {remote.QuantityFilled}/{remote.Quantity}");
            Console.WriteLine($"  AvgPrice        : {remote.AveragePrice?.ToString() ?? "(none)"}");
            Console.WriteLine($"  UpdateTime      : {remote.UpdateTime:yyyy-MM-dd HH:mm:ss}");
        }
        Console.WriteLine();

        // 3) 診斷結論
        Console.WriteLine("-- Diagnosis --");
        if (local is null && remote is null)
        {
            Console.WriteLine("✅ 雙方皆無 — ClientOrderId 從未被使用，冪等鎖處於 ready 狀態。");
            return 0;
        }

        if (local is not null && remote is null)
        {
            Console.WriteLine("⚠️  本地有、交易所無 — 可能情境：");
            Console.WriteLine("    (a) 下單當下交易所 API 呼叫失敗 / 被拒（status 應為 Rejected）");
            Console.WriteLine("    (b) 本地已寫入 Pending 但尚未呼叫交易所");
            Console.WriteLine("    → 冪等保護正常工作，但需檢查為何交易所從未收到");
            return 0;
        }

        if (local is null && remote is not null)
        {
            Console.WriteLine("❌ 本地無、交易所有 — 警告：");
            Console.WriteLine("    (a) 下單成功後 process crash，本地未落地（極罕見）");
            Console.WriteLine("    (b) 同 ClientOrderId 被其他來源使用（外部工具 / 手動 API）");
            Console.WriteLine("    → 建議：從 ExchangeOrderId 走 s61_sync-orders 比對，或手動查清來源");
            return 6;
        }

        // 雙方都有
        var localStatus = local!.Status;
        var remoteStatus = remote!.Status;

        var aligned = localStatus == remoteStatus
            && local.FilledQuantity.Value == remote.QuantityFilled
            && local.Quantity.Value == remote.Quantity;

        if (aligned)
        {
            Console.WriteLine("✅ 本地與交易所狀態完全一致 — 冪等鏈路 healthy。");
            return 0;
        }

        Console.WriteLine("⚠️  雙方都有但狀態不同：");
        if (localStatus != remoteStatus)
            Console.WriteLine($"    Status  : local={localStatus} / exchange={remoteStatus}");
        if (local.Quantity.Value != remote.Quantity)
            Console.WriteLine($"    Qty     : local={local.Quantity.Value} / exchange={remote.Quantity}");
        if (local.FilledQuantity.Value != remote.QuantityFilled)
            Console.WriteLine($"    Filled  : local={local.FilledQuantity.Value} / exchange={remote.QuantityFilled}");
        Console.WriteLine("    → 建議：AccountSynchronizer 下次 tick 自動對齊，或手動 RefreshOrderStatusAsync。");
        return 0;
    }
}
