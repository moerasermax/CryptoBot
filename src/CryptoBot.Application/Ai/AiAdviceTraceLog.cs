namespace CryptoBot.Application.Ai;

/// <summary>
/// 預設 in-memory ring buffer 實作 — Singleton 註冊於 <c>AddApplication</c>。
///
/// 併發安全：用單一 <c>lock</c> 守住雙向串列，<see cref="Record"/> 與 <see cref="GetRecent"/>
/// 皆在鎖內完成 — AI 呼叫頻率低（UI 按鈕觸發），單鎖成本可忽略。
/// </summary>
public sealed class AiAdviceTraceLog : IAiAdviceTraceLog
{
    private const int Capacity = 20;
    private const int CommentaryPreviewMax = 200;

    private readonly object _lock = new();
    private readonly LinkedList<AiAdviceTrace> _traces = new();

    public void Record(AiAdviceResult result)
    {
        var trace = new AiAdviceTrace(
            At: DateTimeOffset.UtcNow,
            Success: result.Success,
            Model: result.Model,
            Error: result.Error,
            CommentaryPreview: Preview(result.Commentary),
            SuggestedParameterCount: result.SuggestedParameters.Count,
            Attempts: result.Attempts);

        lock (_lock)
        {
            _traces.AddFirst(trace);
            while (_traces.Count > Capacity)
            {
                _traces.RemoveLast();
            }
        }
    }

    public IReadOnlyList<AiAdviceTrace> GetRecent(int limit)
    {
        var take = Math.Clamp(limit, 1, Capacity);
        lock (_lock)
        {
            var result = new List<AiAdviceTrace>(Math.Min(take, _traces.Count));
            foreach (var trace in _traces)
            {
                if (result.Count >= take) break;
                result.Add(trace);
            }
            return result;
        }
    }

    private static string Preview(string commentary)
    {
        if (string.IsNullOrEmpty(commentary)) return string.Empty;
        return commentary.Length <= CommentaryPreviewMax
            ? commentary
            : commentary[..CommentaryPreviewMax] + "…";
    }
}
