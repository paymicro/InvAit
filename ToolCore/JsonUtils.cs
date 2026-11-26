using System.Text.Encodings.Web;
using System.Text.Json;

namespace ToolCore;

public static class JsonUtils
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions _compactOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, _jsonOptions);

    public static string SerializeCompact(object value) => JsonSerializer.Serialize(value, _compactOptions);

    public static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch
        {
            return default;
        }
    }

    public static IReadOnlyDictionary<string, object> DeserializeParameters(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json, _jsonOptions) ?? [];
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }

    public static object? GetValue(this IReadOnlyDictionary<string, object> parameters, string key)
    {
        return parameters.TryGetValue(key, out var value) ? value : null;
    }

    public static string? GetString(this IReadOnlyDictionary<string, object> parameters, string key)
    {
        return parameters.GetValue(key)?.ToString();
    }

    public static bool GetBool(this IReadOnlyDictionary<string, object> parameters, string key, bool defaultValue = false)
    {
        var value = parameters.GetValue(key);
        return value?.ToString()?.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => defaultValue
        };
    }

    public static int GetInt(this IReadOnlyDictionary<string, object> parameters, string key, int defaultValue = 0)
    {
        var value = parameters.GetValue(key);
        if (value == null) return defaultValue;

        if (int.TryParse(value.ToString(), out int result))
            return result;

        return defaultValue;
    }

    public static Dictionary<string, string>? GetDictionary(this IReadOnlyDictionary<string, object> parameters, string key)
    {
        var value = parameters.GetValue(key);
        if (value == null) return null;

        try
        {
            var json = JsonSerializer.Serialize(value, _jsonOptions);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Приводит значение payload к <typeparamref name="T"/>, глотая ошибку разбора.
    /// <para>
    /// Годится только там, где <c>null</c> и «не разобралось» РАВНОЗНАЧНЫ (аргументы MCP-сервера,
    /// боковой канал). Инструментам файловой системы он больше не нужен: и <c>edits</c>, и
    /// <c>files</c> разбирают <see cref="EditPayload"/> и <see cref="ReadFilesPayload"/>, потому что
    /// текст STJ («Path: $[0].x | LineNumber: 5») отдавать модели нельзя — эти номера относятся к
    /// копии, которую сериализует именно этот метод, а не к присланному payload.
    /// </para>
    /// </summary>
    public static T? GetObject<T>(this IReadOnlyDictionary<string, object> parameters, string key) where T : class
    {
        var value = parameters.GetValue(key);
        if (value == null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, _jsonOptions), _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Сообщение разбора без ПОЗИЦИИ. `Path: $[0].startLine` называет запись и поле и остаётся,
    /// а `LineNumber`/`BytePositionInLine` отбрасываются: они относятся к pretty-printed копии,
    /// которую сериализует <see cref="JsonShape.TryAsElement"/>, то есть к документу, которого
    /// модель не отправляла (см. docs/dev/edit-matching.md).
    /// Завершающая точка тоже снимается: вызывающий дописывает свою, и «19.. Nothing was read.»
    /// читается как сбой самого хоста.
    /// <para>
    /// Режется по `LineNumber:` БЕЗ ведущего разделителя и с зачисткой остатка: вариант
    /// « | LineNumber:» держался на точном написании одного пробела и одной черты, и любой сдвиг
    /// в форматировании STJ вернул бы модели номер строки в копии — молча, с зелёным тестом.
    /// </para>
    /// </summary>
    internal static string DescribeParseError(Exception exception)
    {
        var message = exception.Message;

        var position = message.IndexOf("LineNumber:", StringComparison.Ordinal);
        if (position >= 0)
        {
            message = message.Substring(0, position).TrimEnd(' ', '|', ',');
        }

        return message.TrimEnd('.', ' ');
    }

    public static T? GetObject<T>(this JsonElement jsonElement) where T : class
    {
        try
        {
            return jsonElement.Deserialize<T>(_jsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
