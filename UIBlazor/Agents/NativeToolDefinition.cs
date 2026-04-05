namespace UIBlazor.Agents;

/// <summary>
/// Описание функции для нативного туллинга
/// </summary>
public class NativeToolDefinition
{
    /// <summary>
    /// Тип всегда функция
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    /// <summary>
    /// Вот и сама функция
    /// </summary>
    [JsonPropertyName("function")]
    public required NativeToolFunction Function { get; set; }
}

/// <summary>
/// Функция для нативного туллинга
/// </summary>
public class NativeToolFunction
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public required string Description { get; set; }

    /// <summary>
    /// Все поля обязательны или нет
    /// </summary>
    [JsonPropertyName("strict")]
    public bool Strict { get; set; } = true;

    [JsonPropertyName("parameters")]
    public required NativeParameters Parameters { get; set; }
}

/// <summary>
/// Параметры функции для нативного туллинга
/// </summary>
public class NativeParameters
{
    /// <summary>
    /// Тип параметра
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; set; } = NativeToolType.String;

    /// <summary>
    /// Все свойства
    /// </summary>
    [JsonPropertyName("properties")]
    public required Dictionary<string, NativePropertyDefinition> Properties { get; set; }

    /// <summary>
    /// Массив с обязательными свойствами. При<see cref="NativeToolFunction.Strict"/> все должны быть обязательны
    /// </summary>
    [JsonPropertyName("required")]
    public List<string> Required { get; set; } = [];

    [JsonPropertyName("additionalProperties")]
    public bool AdditionalProperties { get; } = false;
}

/// <summary>
/// Описание свойства функции для нативного туллинга
/// </summary>
public class NativePropertyDefinition
{
    private string? _singleType;
    private string[]? _unionTypes;

    /// <summary>
    /// Тип свойства. Может быть строкой или массивом строк для union типов.
    /// "string", "number", "integer", "boolean", "array", "object", "null"
    /// Для optional полей в Strict Mode: ["string", "null"]
    /// </summary>
    [JsonPropertyName("type")]
    public object Type
    {
        get => (object?)_unionTypes ?? _singleType ?? NativeToolType.Object;
        set
        {
            switch (value)
            {
                case string s:
                    _singleType = s;
                    _unionTypes = null;
                    break;
                case string[] arr:
                    _unionTypes = arr;
                    _singleType = null;
                    break;
                case JsonElement el when el.ValueKind == JsonValueKind.String:
                    _singleType = el.GetString()!;
                    _unionTypes = null;
                    break;
                case JsonElement el when el.ValueKind == JsonValueKind.Array:
                    _unionTypes = el.EnumerateArray().Select(e => e.GetString()!).ToArray();
                    _singleType = null;
                    break;
            }
        }
    }

    /// <summary>
    /// Установить один тип
    /// </summary>
    public void SetSingleType(string type)
    {
        _singleType = type;
        _unionTypes = null;
    }

    /// <summary>
    /// Установить union типы для optional полей в Strict Mode
    /// </summary>
    public void SetUnionTypes(params string[] types)
    {
        _unionTypes = types;
        _singleType = null;
    }

    /// <summary>
    /// Проверить, является ли тип union (массив типов)
    /// </summary>
    [JsonIgnore]
    public bool IsUnionType => _unionTypes != null;

    /// <summary>
    /// Текст для LLM
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// Если Type = "array"
    /// </summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Items { get; set; }

    /// <summary>
    /// Если Type = "object"
    /// </summary>
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, NativePropertyDefinition>? Properties { get; set; }
}
