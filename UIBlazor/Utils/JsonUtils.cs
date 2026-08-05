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

    public static T DeserializePartialAnonymousType<T>(string partialJson, T anonymousTypeObject)
    {
        if (string.IsNullOrWhiteSpace(partialJson))
        {
            return anonymousTypeObject;
        }

        // Чиним JSON, делая его валидным для стандартного сериализатора
        var sb = new StringBuilder(partialJson.Length + 32);
        sb.Append(partialJson);

        RepairJson(sb);

        try
        {
            // Используем стандартный метод восстановления для анонимных типов
            var repairedJson = sb.ToString();
            return JsonSerializer.Deserialize<T>(repairedJson, _jsonOptions);
        }
        catch (JsonException)
        {
            return anonymousTypeObject;
        }
    }

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

    private static void RepairJson(StringBuilder sb)
    {
        var stack = new Stack<char>();
        var inString = false;
        var isEscaped = false;
        var lastTokenStartIndex = 0;

        // 1. Проход по StringBuilder без создания подстрок
        for (var i = 0; i < sb.Length; i++)
        {
            var c = sb[i];

            if (inString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else
            {
                if (c == '"')
                {
                    inString = true;
                    lastTokenStartIndex = i;
                }
                else if (c == '{' || c == '[')
                {
                    stack.Push(c);
                    lastTokenStartIndex = i + 1;
                }
                else if (c == '}' || c == ']')
                {
                    if (stack.Count > 0) stack.Pop();
                    lastTokenStartIndex = i + 1;
                }
                else if (c == ',' || c == ':')
                {
                    lastTokenStartIndex = i + 1;
                }
            }
        }

        // Смотрим, какой контейнер сейчас является самым верхним
        var currentContainer = stack.Count > 0 ? stack.Peek() : '\0';

        // 2. Обработка незавершенной строки
        if (inString)
        {
            var isValue = false;

            // Если мы внутри массива, то любая строка — это значение, а не ключ
            if (currentContainer == '[')
            {
                isValue = true;
            }
            else
            {
                // Если мы внутри объекта, ищем двоеточие перед строкой
                for (var i = lastTokenStartIndex - 1; i >= 0; i--)
                {
                    var prevChar = sb[i];
                    if (char.IsWhiteSpace(prevChar)) continue;
                    if (prevChar == ':') isValue = true;
                    break;
                }
            }

            if (isValue)
            {
                if (isEscaped && sb[^1] == '\\')
                {
                    sb.Append('\\'); // Дублируем бэкслеш
                }
                sb.Append('"'); // Закрываем кавычку значения
            }
            else
            {
                // Это недописанный ключ в объекте — отсекаем его
                sb.Length = lastTokenStartIndex;
            }
        }

        // 3. Очищаем хвост от висящих запятых, двоеточий и пробелов
        while (sb.Length > 0)
        {
            var lastChar = sb[^1];
            if (lastChar == ',' || lastChar == ':' || char.IsWhiteSpace(lastChar))
            {
                sb.Length--;
            }
            else
            {
                break;
            }
        }

        // 4. Дополнительная проверка на «осиротевший» ключ (актуально только для ОБЪЕКТОВ)
        if (currentContainer == '{' && sb.Length > 0 && sb[^1] == '"')
        {
            var lastQuoteStart = -1;
            for (var i = sb.Length - 2; i >= 0; i--)
            {
                if (sb[i] == '"' && (i == 0 || sb[i - 1] != '\\'))
                {
                    lastQuoteStart = i;
                    break;
                }
            }

            if (lastQuoteStart >= 0)
            {
                var hasColon = false;
                for (var i = lastQuoteStart - 1; i >= 0; i--)
                {
                    var prevChar = sb[i];
                    if (char.IsWhiteSpace(prevChar)) continue;
                    if (prevChar == ':') hasColon = true;
                    break;
                }

                // Если перед строкой в объекте нет двоеточия — это ключ без значения, удаляем его
                if (!hasColon)
                {
                    sb.Length = lastQuoteStart;

                    while (sb.Length > 0)
                    {
                        var lastChar = sb[^1];
                        if (lastChar == ',' || char.IsWhiteSpace(lastChar))
                        {
                            sb.Length--;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
        }

        // 5. Закрываем все открытые контейнеры
        while (stack.Count > 0)
        {
            var openChar = stack.Pop();
            sb.Append(openChar == '{' ? '}' : ']');
        }
    }
}
