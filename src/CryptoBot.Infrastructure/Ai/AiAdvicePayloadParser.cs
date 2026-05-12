using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoBot.Application.Ai;

namespace CryptoBot.Infrastructure.Ai;

/// <summary>
/// S74-C：AI advice JSON payload 解析共用 helper。
///
/// 從 S30 起 <see cref="GeminiAiAdvisorService"/> 內部的 ExtractJson / SanitizeParameters / 三組 alias
/// 隨 S74 / S74-B / S74-C 各路徑被複製過三次（GeminiAiAdvisorService / InteractiveCliAdvisorService /
/// InteractiveChatSession）。本檔抽出共用實作，consumers 統一呼 <see cref="TryParse"/>。
///
/// 公開 API 限定為 <see cref="TryParse"/> 一個 — 內部步驟（fence strip / sanitize / alias）皆封裝。
/// 若未來 wire format 改變（如 AI 改回傳純數字而非三元組）只動本檔。
/// </summary>
internal static class AiAdvicePayloadParser
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static readonly string[] MinAliases = { "min", "Min", "minimum", "Minimum", "from", "From", "start", "Start", "low", "Low" };
    private static readonly string[] MaxAliases = { "max", "Max", "maximum", "Maximum", "to", "To", "end", "End", "high", "High" };
    private static readonly string[] StepAliases = { "step", "Step", "increment", "Increment", "gap", "Gap", "interval", "Interval", "stride", "Stride" };

    /// <summary>
    /// 把 raw 文字（可能含 markdown code fence / 前後綴）解析為 (commentary, parameters)。
    /// 失敗一律以 <paramref name="error"/> 非空回報；commentary / parameters 仍可有部分值（譬如 JSON 解析成功
    /// 但 parameter keys 全被過濾掉時，commentary 留著、parameters 為空 dict）。
    /// </summary>
    /// <param name="rawJson">CLI / HTTP 收到的原始字串。</param>
    /// <param name="expectedKeys">合法參數鍵名清單（StrategyCatalog 提供）；不在清單內的鍵會被剔除。</param>
    /// <param name="commentary">AI commentary 文字；無 commentary 欄位則為 empty string。</param>
    /// <param name="parameters">過濾後的 (key, ParameterGridRange) 字典。</param>
    /// <param name="error">非 null = 失敗原因（人類可讀）；null = 完全成功。</param>
    /// <returns>true = 成功且 parameters 非空；false = error 已填、parameters 可能為空。</returns>
    public static bool TryParse(
        string rawJson,
        IReadOnlyList<string> expectedKeys,
        out string commentary,
        out IReadOnlyDictionary<string, ParameterGridRange> parameters,
        out string? error)
    {
        commentary = string.Empty;
        parameters = new Dictionary<string, ParameterGridRange>();
        error = null;

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            error = "Payload 為空字串。";
            return false;
        }

        string cleaned;
        try
        {
            cleaned = ExtractJson(rawJson);
        }
        catch (Exception ex)
        {
            error = $"ExtractJson 失敗：{ex.Message}";
            return false;
        }

        AiAdvicePayloadDto? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AiAdvicePayloadDto>(cleaned, JsonOpts);
        }
        catch (JsonException jex)
        {
            error = $"JSON 解析失敗：{jex.Message}";
            return false;
        }

        if (payload is null)
        {
            error = "JSON 解析回 null payload。";
            return false;
        }

        commentary = payload.Commentary ?? string.Empty;
        var sanitized = SanitizeParameters(payload.Parameters, expectedKeys);

        if (sanitized.Count == 0)
        {
            if (payload.Parameters is { Count: > 0 } rawParams)
            {
                var rawKeys = string.Join(", ", rawParams.Keys);
                var expected = string.Join(", ", expectedKeys);
                error = $"回傳的參數鍵不在合法清單。AI 給的鍵：[{rawKeys}]；期待的鍵：[{expected}]。";
            }
            else
            {
                error = "Payload 無 parameters 欄位或為空。";
            }
            return false;
        }

        parameters = sanitized;
        return true;
    }

    /// <summary>
    /// 剝除 markdown code fence（```json … ``` 或 ``` … ```），回原始 JSON 字串。
    /// 不含 fence 即原樣 trim 後回。
    /// </summary>
    public static string ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (end > 7) return trimmed[7..end].Trim();
        }
        else if (trimmed.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (end > 3) return trimmed[3..end].Trim();
        }
        return trimmed;
    }

    private static IReadOnlyDictionary<string, ParameterGridRange> SanitizeParameters(
        IDictionary<string, JsonElement>? raw, IReadOnlyList<string> expectedKeys)
    {
        var result = new Dictionary<string, ParameterGridRange>();
        if (raw is null) return result;
        var allowed = new HashSet<string>(expectedKeys, StringComparer.OrdinalIgnoreCase);
        var keyMap = expectedKeys.ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var kv in raw)
        {
            if (!allowed.Contains(kv.Key)) continue;
            if (!keyMap.TryGetValue(kv.Key, out var officialKey)) continue;
            if (TryParseGridRange(kv.Value, out var range))
            {
                result[officialKey] = range;
            }
        }
        return result;
    }

    private static bool TryParseGridRange(JsonElement value, out ParameterGridRange range)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
            {
                var v = value.GetDecimal();
                range = new ParameterGridRange(v, v, 1m);
                return true;
            }
            case JsonValueKind.Object:
            {
                var min = TryGetDecimalAny(value, MinAliases);
                var max = TryGetDecimalAny(value, MaxAliases);
                var step = TryGetDecimalAny(value, StepAliases);
                if (min is null && max is null) { range = default!; return false; }
                var minVal = min ?? max!.Value;
                var maxVal = max ?? min!.Value;
                if (maxVal < minVal) (minVal, maxVal) = (maxVal, minVal);
                var stepVal = step ?? 1m;
                if (stepVal <= 0m) stepVal = 1m;
                range = new ParameterGridRange(minVal, maxVal, stepVal);
                return true;
            }
            default:
                range = default!;
                return false;
        }
    }

    private static decimal? TryGetDecimalAny(JsonElement obj, string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var prop)) continue;
            switch (prop.ValueKind)
            {
                case JsonValueKind.Number:
                    return prop.GetDecimal();
                case JsonValueKind.String when decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d):
                    return d;
            }
        }
        return null;
    }

    private sealed record AiAdvicePayloadDto(
        [property: JsonPropertyName("commentary")] string? Commentary,
        [property: JsonPropertyName("parameters")] IDictionary<string, JsonElement>? Parameters);
}
