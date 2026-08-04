using System.Text.Encodings.Web;

namespace UIBlazor.Utils;

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

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, _jsonOptions);

    public static T DeserializeAnonymousType<T>(string json, T anonymousTypeObject) => JsonSerializer.Deserialize<T>(json, _jsonOptions);

    public static string PrettyPrintFormat(string minifiedJson)
    {
        using var document = JsonDocument.Parse(minifiedJson);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    public static IReadOnlyDictionary<string, object> DeserializeParameters(string json)
    {
        try
        {
            var document = JsonDocument.Parse(json);
            var result = new Dictionary<string, object>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = ConvertJsonElement(property.Value);
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }

    private static object ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element.GetRawText()
        };
    }

    extension(IReadOnlyDictionary<string, object> parameters)
    {
        public object? GetValue(string key)
        {
            return parameters.GetValueOrDefault(key);
        }

        public string? GetString(string key)
        {
            return parameters.GetValue(key)?.ToString();
        }

        public bool GetBool(string key, bool defaultValue = false)
        {
            var value = parameters.GetValue(key);
            return value?.ToString()?.ToLowerInvariant() switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" => false,
                _ => defaultValue
            };
        }

        public int GetInt(string key, int defaultValue = 0)
        {
            var value = parameters.GetValue(key);
            if (value == null) return defaultValue;

            return int.TryParse(value.ToString(), out var result)
                ? result
                : defaultValue;
        }

        public T? GetObject<T>(string key) where T : class
        {
            var value = parameters.GetValue(key);
            if (value == null) return null;

            try
            {
                return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, _jsonOptions), _jsonOptions);
            }
            catch
            {
                return null;
            }
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
