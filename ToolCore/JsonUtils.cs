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

    public static T? GetObject<T>(this IReadOnlyDictionary<string, object> parameters, string key) where T : class
        => parameters.GetObject<T>(key, out _);

    /// <summary>
    /// То же, но сообщает ПОЧЕМУ payload не прочитался.
    /// <para>
    /// Без этого вызывающий видит только <c>null</c> и вынужден УГАДЫВАТЬ причину в тексте отказа.
    /// Ровно так и появилось «each edit needs 'oldStr' and 'newStr' strings» на payload, где оба
    /// поля были на месте: настоящая ошибка (например, <c>"approximateLine": ""</c> не является
    /// числом) проглатывалась, модель шла исправлять не то, а разработчик не имел ни одной зацепки.
    /// </para>
    /// </summary>
    public static T? GetObject<T>(this IReadOnlyDictionary<string, object> parameters, string key, out string? error) where T : class
    {
        error = null;
        var value = parameters.GetValue(key);
        if (value == null)
        {
            error = $"'{key}' is missing";
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, _jsonOptions), _jsonOptions);
        }
        catch (Exception e)
        {
            // Текст System.Text.Json называет поле и позицию — именно то, что нужно вызывающему.
            error = e.Message;
            return null;
        }
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
