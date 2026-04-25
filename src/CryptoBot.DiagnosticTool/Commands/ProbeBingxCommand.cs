using CryptoBot.Application.Common;
using CryptoBot.Application.Common.Exceptions;
using CryptoBot.Application.Common.Interfaces;
using CryptoBot.Domain.Aggregates.OrderAggregate;
using CryptoBot.Domain.Enums;
using CryptoBot.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBot.DiagnosticTool.Commands;

/// <summary>
/// S66-A T0：BingX 冪等鏈路探針。回補 S66-A 完工時跳過的「實機探針」步驟。
///
/// 三段式探測：
///   Step 1：用一個全新 ClientOrderId 送一筆遠離市價的限價買單（不會成交）
///   Step 2：用「完全相同」的 ClientOrderId 再送一次 — 觀察 BingX 怎麼回應
///           a. 走例外路徑：捕獲 <see cref="DuplicateClientOrderIdException"/>，列印 RawErrorCode + RawErrorMessage
///           b. 走 Reject 路徑：列印 <see cref="Order.RejectReason"/>（代表本地嗅探沒命中、需擴充）
///           c. 第二筆竟然成功：代表 BingX 端不對 ClientOrderId 去重（極罕見、需警示）
///   Step 3：用一個從未使用過的隨機 ClientOrderId 呼叫 <c>GetOrderByClientOrderIdAsync</c> —
///           觀察 not-found 回 null 或拋例外
///
/// 全部結束後 cancel 第一筆探測單。
///
/// 輸出格式設計成「使用者可一鍵複製到對話框」回報給 PM，作為 <c>Institutional_Memory</c> §X 的探針資產來源。
///
/// **安全鎖**：本指令偵測到 <see cref="TradingMode.Live"/> 直接拒跑 — 探針會送真單，雖然金額小，
/// 但 Live 一律走 Demo 是規矩。
/// </summary>
public sealed class ProbeBingxCommand : IDiagnosticCommand
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ProbeBingxCommand(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string Name => "probe-bingx";
    public IReadOnlyList<string> Aliases => new[] { "s66a_probe", "probe" };
    public string Description => "Probe BingX raw error codes for duplicate / not-found ClientOrderId (DEMO ONLY).";
    public string Usage => "probe-bingx [Symbol]";

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var exchange = sp.GetRequiredService<IExchangeClient>();

        // ===== 安全鎖：禁 Live =====
        if (exchange.CurrentMode != TradingMode.Demo)
        {
            Console.Error.WriteLine($"❌ probe-bingx 嚴禁在 {exchange.CurrentMode} 模式執行 — 僅允許 Demo (VST)。");
            Console.Error.WriteLine("   原因：探針會在交易所端建立真實掛單。實機驗證一律走 Demo。");
            Console.Error.WriteLine("   請先切換到 Demo 環境（appsettings 或 EnvironmentSwitcher）後再執行。");
            return 4;
        }

        // ===== 解析 Symbol =====
        var symbolStr = args.Length >= 1 ? args[0] : "BTC-USDT";
        Symbol symbol;
        try { symbol = Symbol.Parse(symbolStr); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Invalid symbol '{symbolStr}': {ex.Message}");
            return 2;
        }

        Console.WriteLine("=== BingX Idempotency Probe (S66-A T0) ===");
        Console.WriteLine($"Mode    : {exchange.CurrentMode}");
        Console.WriteLine($"Symbol  : {symbol.BingXFormat}");
        Console.WriteLine($"RunAt   : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine();

        // ===== 取交易規則 + 市價 =====
        SymbolTradingRules rules;
        Price markPrice;
        try
        {
            rules = await exchange.GetTradingRulesAsync(symbol, ct).ConfigureAwait(false);
            markPrice = await exchange.GetMarkPriceAsync(symbol, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] 取規則 / 市價失敗: {ex.GetType().Name} -> {ex.Message}");
            return 5;
        }

        // 用步進值 floor 一個遠低於市價的限價（半價），避免成交
        var rawLimit = markPrice.Value * 0.5m;
        var tick = rules.TickSize > 0 ? rules.TickSize : 0.01m;
        var safeLimitValue = Math.Floor(rawLimit / tick) * tick;
        if (safeLimitValue <= 0) safeLimitValue = decimal.Round(rawLimit, 2);
        var safeLimit = Price.Create(safeLimitValue);

        var probeQtyValue = rules.MinQuantity > 0 ? rules.MinQuantity : 0.001m;
        var probeQty = Quantity.Create(probeQtyValue);
        var probeCid = $"probe_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        Console.WriteLine($"Market  : {markPrice.Value}");
        Console.WriteLine($"Tick    : {rules.TickSize}, Step: {rules.StepSize}, MinQty: {rules.MinQuantity}");
        Console.WriteLine($"Limit   : {safeLimit.Value}  (= 50%-of-market floor by tick, WON'T fill)");
        Console.WriteLine($"Qty     : {probeQty.Value}");
        Console.WriteLine($"CID     : {probeCid}");
        Console.WriteLine();

        // ===== Step 1: First placement =====
        Console.WriteLine("-- Step 1: First placement (expect success) --");
        Order? firstOrder = null;
        try
        {
            firstOrder = Order.CreateLimitOrder(
                symbol: symbol,
                side: OrderSide.Buy,
                positionSide: PositionSide.Long,
                quantity: probeQty,
                limitPrice: safeLimit,
                strategyId: null,
                clientOrderId: probeCid);

            await exchange.PlaceOrderAsync(firstOrder, ct).ConfigureAwait(false);

            if (firstOrder.Status == OrderStatus.Rejected)
            {
                Console.WriteLine($"  ⚠️  First order REJECTED. Reason: {firstOrder.RejectReason}");
                Console.WriteLine("     可能命中交易規則（minNotional 等）。Probe 終止 — 沒有第一筆成功單就無法測 duplicate。");
                return 6;
            }

            Console.WriteLine($"  Status          : {firstOrder.Status}");
            Console.WriteLine($"  ExchangeOrderId : {firstOrder.ExchangeOrderId ?? "(none)"}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [FATAL] Step 1 unexpected: {ex.GetType().Name} -> {ex.Message}");
            return 7;
        }
        Console.WriteLine();

        // ===== Step 2: Replay with same cid =====
        Console.WriteLine("-- Step 2: Replay with SAME ClientOrderId (expect duplicate signal) --");
        Order? dupOrder = null;
        try
        {
            dupOrder = Order.CreateLimitOrder(
                symbol: symbol,
                side: OrderSide.Buy,
                positionSide: PositionSide.Long,
                quantity: probeQty,
                limitPrice: safeLimit,
                strategyId: null,
                clientOrderId: probeCid);

            await exchange.PlaceOrderAsync(dupOrder, ct).ConfigureAwait(false);

            if (dupOrder.Status == OrderStatus.Rejected)
            {
                Console.WriteLine("  ⚠️  Path-B (Reject without exception):");
                Console.WriteLine("     代表 BingX 回了 Error response 但 IsDuplicateClientOrderIdError 沒嗅探到 duplicate 字樣。");
                Console.WriteLine($"     Order.RejectReason : {dupOrder.RejectReason}");
                Console.WriteLine();
                Console.WriteLine("     → 行動：把 RejectReason 全文回報 PM；若是 duplicate 但訊息措辭不同，");
                Console.WriteLine("       須擴充 IsDuplicateClientOrderIdError 的字串清單，最終升級為 errorCode 比對。");
            }
            else
            {
                // BingX 居然接受了 — 代表它不對 cid 去重
                Console.WriteLine("  ❗ Path-C (UNEXPECTED success):");
                Console.WriteLine("     第二次下單成功 — BingX 端沒對 ClientOrderId 去重？");
                Console.WriteLine($"     Status          : {dupOrder.Status}");
                Console.WriteLine($"     ExchangeOrderId : {dupOrder.ExchangeOrderId ?? "(none)"}");
                Console.WriteLine("     → 結論：S66-A 的冪等性完全靠本地 DB Unique 索引，不能依賴交易所端去重。");
            }
        }
        catch (DuplicateClientOrderIdException dup)
        {
            Console.WriteLine("  ✅ Path-A (DuplicateClientOrderIdException):");
            Console.WriteLine($"     RawErrorCode    : {dup.RawErrorCode ?? "(null)"}");
            Console.WriteLine($"     RawErrorMessage : {dup.RawErrorMessage ?? "(null)"}");
            Console.WriteLine($"     InnerException  : {dup.InnerException?.GetType().Name ?? "(none)"}");
            if (dup.InnerException is not null)
                Console.WriteLine($"     InnerMessage    : {dup.InnerException.Message}");
            Console.WriteLine();
            Console.WriteLine("     → 行動：把 RawErrorCode 寫進 Institutional_Memory §S66-A，");
            Console.WriteLine("       把 BingXExchangeClient.IsDuplicateClientOrderIdError 從字串嗅探升級為");
            Console.WriteLine("       `errorCode == <code>` 的精確比對。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❗ Path-D (Unexpected exception): {ex.GetType().Name}");
            Console.WriteLine($"     Message: {ex.Message}");
            Console.WriteLine($"     StackTop: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            Console.WriteLine("     → 把整段例外訊息回報 PM。");
        }
        Console.WriteLine();

        // ===== Step 3: GetOrderByClientOrderIdAsync with non-existent cid =====
        Console.WriteLine("-- Step 3: GetOrderByClientOrderIdAsync with NON-EXISTENT cid --");
        var bogusCid = "probe_404_" + Guid.NewGuid().ToString("N").Substring(0, 12);
        Console.WriteLine($"  Bogus CID : {bogusCid}");
        try
        {
            var snapshot = await exchange.GetOrderByClientOrderIdAsync(symbol, bogusCid, ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                Console.WriteLine("  ✅ Path-A: 回 null — 符合 GetOrderByClientOrderIdAsync 的 not-found 契約。");
            }
            else
            {
                Console.WriteLine("  ❗ Path-B: 不應該找到資料，卻回了 snapshot：");
                Console.WriteLine($"     ExchangeOrderId : {snapshot.ExchangeOrderId}");
                Console.WriteLine($"     Status          : {snapshot.Status}");
                Console.WriteLine("     → BingX 對未知 cid 回了某筆既存訂單？把全部欄位回報 PM。");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine("  ⚠️  Path-C: 拋例外（not-found 嗅探沒捕到）：");
            Console.WriteLine($"     Type    : {ex.GetType().Name}");
            Console.WriteLine($"     Message : {ex.Message}");
            Console.WriteLine("     → 把例外訊息回報 PM；補 GetOrderByClientOrderIdAsync 的 not-found 字串清單");
            Console.WriteLine("       （目前是 \"not exist\" / \"not found\" / \"110416\"）。");
        }
        Console.WriteLine();

        // ===== Cleanup =====
        Console.WriteLine("-- Cleanup --");
        if (firstOrder is not null && firstOrder.IsActive && !string.IsNullOrEmpty(firstOrder.ExchangeOrderId))
        {
            try
            {
                await exchange.CancelOrderAsync(firstOrder, ct).ConfigureAwait(false);
                Console.WriteLine($"  ✅ Cancelled probe order ExchangeOrderId={firstOrder.ExchangeOrderId}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠️  Cancel failed: {ex.Message}");
                Console.WriteLine($"     請手動於 BingX Demo UI 撤掉 ExchangeOrderId={firstOrder.ExchangeOrderId}");
            }
        }
        else
        {
            Console.WriteLine("  (no active probe order to cancel)");
        }

        // 第二筆若意外成功，也要 cancel
        if (dupOrder is not null && dupOrder.IsActive && !string.IsNullOrEmpty(dupOrder.ExchangeOrderId))
        {
            try
            {
                await exchange.CancelOrderAsync(dupOrder, ct).ConfigureAwait(false);
                Console.WriteLine($"  ✅ Cancelled duplicate-side probe order ExchangeOrderId={dupOrder.ExchangeOrderId}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠️  Cancel of dup-side order failed: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== Probe complete — 請把整段輸出貼回給 PM 補登 Institutional_Memory ===");
        return 0;
    }
}
