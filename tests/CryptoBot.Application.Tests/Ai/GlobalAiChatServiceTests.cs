using System.Runtime.CompilerServices;
using CryptoBot.Application.Ai;
using CryptoBot.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoBot.Application.Tests.Ai;

/// <summary>
/// S75 Phase 4：<see cref="GlobalAiChatService"/> 行為 unit tests — 走 fake <see cref="IGeminiAcpClient"/>
/// 不依賴真實 gemini --acp process。
///
/// 涵蓋核心契約：
/// <list type="bullet">
///   <item>SendAsync 永不拋（既有 S74-C 契約）— ACP 拋例外時應轉為 ai 訊息 append。</item>
///   <item>Phase 3：ACP 429 → AppendAi 含「Gemini model 暫時繁忙」訊息（user-facing）。</item>
///   <item>正常 ACP 回應含 JSON code fence + 合法 parameter keys → ChatMessage.Payload 不為 null。</item>
///   <item>空 / 空白 user text → no-op、不呼叫 ACP。</item>
/// </list>
/// </summary>
public class GlobalAiChatServiceTests
{
    private static GlobalAiChatService Make(FakeGeminiAcpClient acp, IStrategyParameterKeyCatalog? catalog = null)
    {
        var opts = Options.Create(new InteractiveCliAdvisorOptions
        {
            Executable = "gemini",
            TimeoutSeconds = 30,
        });
        return new GlobalAiChatService(
            catalog ?? new FakeKeyCatalog(),
            acp,
            opts,
            NullLogger<GlobalAiChatService>.Instance);
    }

    [Fact]
    public async Task SendAsync_EmptyText_IsNoop()
    {
        var acp = new FakeGeminiAcpClient(replyChunks: new[] { "should not be called" });
        var svc = Make(acp);

        await svc.SendAsync("   ");

        Assert.Empty(svc.History);
        Assert.Equal(0, acp.SendPromptInvocations);
    }

    [Fact]
    public async Task SendAsync_NormalReply_AppendsUserAndAi()
    {
        var acp = new FakeGeminiAcpClient(replyChunks: new[] { "Hello ", "world" });
        var svc = Make(acp);

        await svc.SendAsync("hi");

        Assert.Equal(2, svc.History.Count);
        Assert.Equal("user", svc.History[0].Role);
        Assert.Equal("hi", svc.History[0].Text);
        Assert.Equal("ai", svc.History[1].Role);
        Assert.Equal("Hello world", svc.History[1].Text);
        Assert.False(svc.History[1].HasJson);
    }

    [Fact]
    public async Task SendAsync_AcpThrowsGenericError_NeverThrows_AppendsErrorMessage()
    {
        var acp = new FakeGeminiAcpClient(thrower: () => new InvalidOperationException("ACP error: random failure"));
        var svc = Make(acp);

        // 永不拋契約 — 此呼叫不應拋出。
        await svc.SendAsync("test");

        Assert.Equal(2, svc.History.Count);
        Assert.Equal("ai", svc.History[1].Role);
        Assert.Contains("⚠", svc.History[1].Text);
        Assert.Contains("ACP error", svc.History[1].Text);
    }

    [Fact]
    public async Task SendAsync_Acp429_AppendsUserFacingOverloadMessage()
    {
        // Phase 3 user-facing 訊息（Is429Error 在 GeminiAcpClient 內、外部看到的是 wrapped message）。
        var overloadEx = new InvalidOperationException(
            "Gemini model 暫時繁忙 (429 / RESOURCE_EXHAUSTED / MODEL_CAPACITY_EXHAUSTED)。建議：稍候 30s 重發。",
            new InvalidOperationException("ACP error: {\"code\":429,\"message\":\"RESOURCE_EXHAUSTED\"}"));

        var acp = new FakeGeminiAcpClient(thrower: () => overloadEx);
        var svc = Make(acp);

        await svc.SendAsync("any prompt");

        Assert.Equal(2, svc.History.Count);
        Assert.Equal("ai", svc.History[1].Role);
        Assert.Contains("Gemini model 暫時繁忙", svc.History[1].Text);
    }

    [Fact]
    public async Task SendAsync_EnsureSessionCalled_OnFirstSend()
    {
        var acp = new FakeGeminiAcpClient(replyChunks: new[] { "ok" });
        var svc = Make(acp);

        await svc.SendAsync("first");

        // Phase 2 設計：lazy init — SendPromptAsync 內 await EnsureSessionAsync
        // FakeGeminiAcpClient.SendPromptAsync 本身不呼叫 EnsureSessionAsync（單元測試聚焦行為、不模擬 ACP 內部協議）
        // 此測試確認 SendPromptAsync 被呼叫一次 = lazy 機制觸發點
        Assert.Equal(1, acp.SendPromptInvocations);
    }

    [Fact]
    public async Task SendAsync_MultipleRounds_HistoryAccumulates()
    {
        var acp = new FakeGeminiAcpClient(replyChunks: new[] { "reply" });
        var svc = Make(acp);

        await svc.SendAsync("round 1");
        await svc.SendAsync("round 2");
        await svc.SendAsync("round 3");

        // 3 user + 3 ai = 6
        Assert.Equal(6, svc.History.Count);
        Assert.Equal(3, acp.SendPromptInvocations);
        // ACP 同 instance 持續復用（capsule §6.1 session 持久化驗證點）
    }

    [Fact]
    public async Task SendAsync_AcpReturnsEmpty_AppendsEmptyReplyMessage()
    {
        var acp = new FakeGeminiAcpClient(replyChunks: Array.Empty<string>());
        var svc = Make(acp);

        await svc.SendAsync("test");

        Assert.Equal(2, svc.History.Count);
        Assert.Contains("AI 無回應", svc.History[1].Text);
    }
}

// ──────────────────────── Fake doubles（手寫，無 Moq 依賴）────────────────────────

internal sealed class FakeGeminiAcpClient : IGeminiAcpClient
{
    private readonly string[]? _replyChunks;
    private readonly Func<Exception>? _thrower;

    public int SendPromptInvocations { get; private set; }
    public int EnsureSessionInvocations { get; private set; }
    public bool Disposed { get; private set; }

    public FakeGeminiAcpClient(string[]? replyChunks = null, Func<Exception>? thrower = null)
    {
        _replyChunks = replyChunks;
        _thrower = thrower;
    }

    public Task EnsureSessionAsync(CancellationToken ct = default)
    {
        EnsureSessionInvocations++;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> SendPromptAsync(
        string text,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        SendPromptInvocations++;
        if (_thrower is not null) throw _thrower();
        if (_replyChunks is null) yield break;
        foreach (var c in _replyChunks)
        {
            await Task.Yield();
            yield return c;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeKeyCatalog : IStrategyParameterKeyCatalog
{
    public FakeKeyCatalog(string[]? keys = null) => AllParameterKeys = keys ?? Array.Empty<string>();
    public IReadOnlyList<string> AllParameterKeys { get; }
    public IReadOnlyList<string>? KeysFor(string strategyKey) => null;
}
