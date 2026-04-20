using Microsoft.AspNetCore.SignalR;

namespace CryptoBot.ConsoleApp.Realtime;

/// <summary>
/// SignalR Hub — 前端透過 <c>/hub/trade</c> 連進來即可收到兩種推播：
/// <list type="bullet">
///   <item><c>TradeFilled</c> — 策略開/平倉成交時即時推。</item>
///   <item><c>StatsUpdate</c> — 2 秒一次的儀表板總覽心跳（總資產 / 今日 P&amp;L / 活躍策略數）。</item>
/// </list>
/// 真正負責 push 的不是這個類，而是 <see cref="SignalRRealtimeBroadcaster"/>
/// 透過 <see cref="IHubContext{T}"/> 發 message。Hub 自己純粹是連線端點。
/// </summary>
public sealed class TradeHub : Hub
{
    // 無自訂 client→server 方法。前端只 listen，不 invoke。
}
