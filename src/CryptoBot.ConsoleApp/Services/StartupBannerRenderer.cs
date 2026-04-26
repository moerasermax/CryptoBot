using CryptoBot.Application.Synchronization;

namespace CryptoBot.ConsoleApp.Services;

/// <summary>
/// S66-E：把 <see cref="StartupCheckResult"/> 渲染成 ASCII banner，印到 stdout。
///
/// 故意放在 ConsoleApp（Composition Root），**不**讓 Application 層碰 <c>System.Console</c>
/// （IRON ⑥ 抽象隔離）。本類別純函式 + 純輸出，無狀態無外部依賴。
/// </summary>
public static class StartupBannerRenderer
{
    private const int BannerWidth = 56;

    /// <summary>把健檢結果渲染為 banner 並寫到 stdout。</summary>
    public static void Render(StartupCheckResult result)
    {
        var lines = BuildLines(result);
        Console.WriteLine();
        foreach (var line in lines) Console.WriteLine(line);
        Console.WriteLine();
    }

    /// <summary>純函式版本 — 給單元測試用，不寫 stdout。</summary>
    internal static IReadOnlyList<string> BuildLines(StartupCheckResult result)
    {
        var lines = new List<string>
        {
            Box('╔', '═', '╗'),
            Center("CryptoBot Pre-flight Check"),
            Box('╠', '═', '╣'),
            Field("Exchange", result.ExchangeName),
            Field("Mode", FormatMode(result.Mode)),
        };

        if (result.MeasurementSucceeded)
        {
            lines.Add(Field("Round-trip", $"{result.RoundTripMs}ms"));
            lines.Add(Field("Clock skew", FormatSkew(result.SkewStatus, result.OffsetMs!.Value)));
        }
        else
        {
            lines.Add(Field("Clock skew", "❓ MEASUREMENT FAILED"));
            if (!string.IsNullOrEmpty(result.ErrorMessage))
                lines.Add(Field("Error", Truncate(result.ErrorMessage, BannerWidth - 12)));
        }

        if (!string.IsNullOrEmpty(result.ActionAdvice))
        {
            lines.Add(Empty());
            foreach (var wrapped in WrapText(result.ActionAdvice, BannerWidth - 4))
                lines.Add($"║ {wrapped.PadRight(BannerWidth - 4)} ║");
        }

        lines.Add(Box('╚', '═', '╝'));
        return lines;
    }

    private static string FormatMode(CryptoBot.Application.Common.TradingMode mode) =>
        mode == CryptoBot.Application.Common.TradingMode.Live
            ? "🔴 LIVE (USDT)"
            : "🟢 DEMO (VST)";

    private static string FormatSkew(SkewStatus status, long offsetMs) =>
        status switch
        {
            SkewStatus.Safe => $"✅ SAFE ({offsetMs:+0;-0;0}ms)",
            SkewStatus.Warning => $"⚠️  WARNING ({offsetMs:+0;-0;0}ms)",
            SkewStatus.Unsafe => $"❌ UNSAFE ({offsetMs:+0;-0;0}ms — RiskManager 會擋下單)",
            _ => "❓ unknown",
        };

    private static string Box(char left, char fill, char right) =>
        left + new string(fill, BannerWidth - 2) + right;

    private static string Empty() =>
        "║" + new string(' ', BannerWidth - 2) + "║";

    private static string Center(string text)
    {
        var inner = BannerWidth - 2;
        var displayLen = DisplayLength(text);
        if (displayLen >= inner) return "║" + text + "║";
        var pad = inner - displayLen;
        var left = pad / 2;
        var right = pad - left;
        return "║" + new string(' ', left) + text + new string(' ', right) + "║";
    }

    private static string Field(string label, string value)
    {
        // "║ Label       : value...                                    ║"
        var labelPart = $" {label,-12}: ";
        var inner = BannerWidth - 2 - DisplayLength(labelPart);
        var truncated = Truncate(value, inner);
        var displayLen = DisplayLength(truncated);
        var pad = Math.Max(0, inner - displayLen);
        return "║" + labelPart + truncated + new string(' ', pad) + "║";
    }

    /// <summary>粗略估算字串「視覺寬度」—— ASCII = 1 格，emoji / CJK = 2 格。</summary>
    private static int DisplayLength(string s)
    {
        var width = 0;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current!;
            // emoji + 大部分 CJK 視為寬度 2
            var firstRune = element.EnumerateRunes().FirstOrDefault();
            width += IsWide(firstRune.Value) ? 2 : 1;
        }
        return width;
    }

    private static bool IsWide(int codepoint)
    {
        // CJK Unified Ideographs / Hiragana / Katakana / Hangul / 全形 / Emoji 範圍粗略判斷
        return codepoint >= 0x1100 && (
            codepoint <= 0x115F ||
            (codepoint >= 0x2E80 && codepoint <= 0x9FFF) ||
            (codepoint >= 0xA000 && codepoint <= 0xA4CF) ||
            (codepoint >= 0xAC00 && codepoint <= 0xD7A3) ||
            (codepoint >= 0xF900 && codepoint <= 0xFAFF) ||
            (codepoint >= 0xFE30 && codepoint <= 0xFE4F) ||
            (codepoint >= 0xFF00 && codepoint <= 0xFF60) ||
            (codepoint >= 0xFFE0 && codepoint <= 0xFFE6) ||
            (codepoint >= 0x1F300 && codepoint <= 0x1FAFF));
    }

    private static string Truncate(string s, int max) =>
        DisplayLength(s) <= max ? s : s.Substring(0, Math.Min(s.Length, max - 1)) + "…";

    /// <summary>簡易自動換行 — 依 display width 切，盡量在空格 / 中文逗號處斷。</summary>
    private static IEnumerable<string> WrapText(string text, int maxWidth)
    {
        if (DisplayLength(text) <= maxWidth)
        {
            yield return text;
            yield break;
        }

        var current = "";
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var ch = (string)enumerator.Current!;
            if (DisplayLength(current + ch) > maxWidth)
            {
                yield return current;
                current = ch;
            }
            else
            {
                current += ch;
            }
        }
        if (current.Length > 0) yield return current;
    }
}
