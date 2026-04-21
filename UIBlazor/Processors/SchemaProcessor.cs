using System.Collections;
using Shared.Contracts.Mcp;

namespace UIBlazor.Processors;

public static class SchemaProcessor
{
    private const int _maxDepth = 5;

    /// <summary>
    /// Deserializes a JsonElement representing a JSON Schema into a JsonSchemaProperty object graph.
    /// </summary>
    /// <param name="schemaElement">The JsonElement containing the schema.</param>
    /// <param name="currentDepth">The current recursion depth (used internally).</param>
    /// <param name="maxDepth">The maximum allowed nesting depth.</param>
    /// <returns>A JsonSchemaProperty object representing the schema, or null if the input is invalid.</returns>
    public static JsonSchemaProperty? DeserializeSchema(JsonElement? schemaElement, int currentDepth = 0, int maxDepth = _maxDepth)
    {
        if (!schemaElement.HasValue || schemaElement.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (currentDepth > maxDepth)
        {
            return null;
        }

        var schema = schemaElement.Value;
        var property = new JsonSchemaProperty();

        if (schema.TryGetProperty("type", out var typeElement))
        {
            property.Type = typeElement.GetString();
        }

        if (schema.TryGetProperty("description", out var descriptionElement))
        {
            property.Description = descriptionElement.GetString();
        }

        // Handle constraints
        if (schema.TryGetProperty("enum", out var enumElement) && enumElement.ValueKind == JsonValueKind.Array)
        {
            property.EnumValues = [];
            foreach (var val in enumElement.EnumerateArray())
            {
                // Store raw values, conversion happens later if needed
                property.EnumValues.Add(val.Clone());
            }
        }

        if (schema.TryGetProperty("minimum", out var minElement))
        {
            property.Minimum = minElement.GetDouble();
        }
        if (schema.TryGetProperty("maximum", out var maxElement))
        {
            property.Maximum = maxElement.GetDouble();
        }
        if (schema.TryGetProperty("minLength", out var minLengthElement))
        {
            property.MinLength = minLengthElement.GetInt32();
        }
        if (schema.TryGetProperty("maxLength", out var maxLengthElement))
        {
            property.MaxLength = maxLengthElement.GetInt32();
        }
        if (schema.TryGetProperty("pattern", out var patternElement))
        {
            property.Pattern = patternElement.GetString();
        }
        if (schema.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
        {
            property.Required = [.. requiredElement.EnumerateArray().Select(r => r.ToString())];
        }

        // Recursively handle nested structures
        if (property.Type == "object" && schema.TryGetProperty("properties", out var propsElement) && propsElement.ValueKind == JsonValueKind.Object)
        {
            property.Properties = [];
            foreach (var prop in propsElement.EnumerateObject())
            {
                var nestedProp = DeserializeSchema(prop.Value, currentDepth + 1, maxDepth);
                if (nestedProp != null)
                {
                    property.Properties[prop.Name] = nestedProp;
                }
            }
        }

        if (property.Type == "array" && schema.TryGetProperty("items", out var itemsElement))
        {
            property.Items = DeserializeSchema(itemsElement, currentDepth + 1, maxDepth);
        }

        return property;
    }

    /// <summary>
    /// Build a readable schema description for LLM prompt
    /// </summary>
    public static string BuildSchemaDescription(string toolName, McpToolConfig toolConfig)
    {
        try
        {
            var schemaElement = toolConfig.InputSchema;
            var schemaProperty = DeserializeSchema(schemaElement);
            if (schemaProperty == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            var exampleObj = GenerateObjectExample(schemaProperty);

            sb.AppendLine("For example:");
            sb.AppendLine($"<function name=\"{toolName}\">");
            sb.AppendLine(JsonUtils.Serialize(exampleObj));
            sb.AppendLine("</function>");

            sb.AppendLine("*Properties schema:*");
            AppendSchemaDescription(sb, schemaProperty, parentPath: "");

            return sb.ToString();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public static void AppendSchemaDescription(StringBuilder sb, JsonSchemaProperty prop, string parentPath, int depth = 0)
    {
        if (prop.Properties != null)
        {
            foreach (var nestedPropKvp in prop.Properties)
            {
                var required = prop.Required is not null
                    ? prop.Required.Contains(nestedPropKvp.Key)
                    : false;
                var currentPath = string.IsNullOrEmpty(parentPath) ? nestedPropKvp.Key : $"{parentPath}.{nestedPropKvp.Key}";
                var nestedProp = nestedPropKvp.Value;

                var typeInfo = nestedProp.Type ?? "unknown";
                if (nestedProp.EnumValues != null && nestedProp.EnumValues.Count > 0)
                {
                    typeInfo += $" (enum: {string.Join(", ", nestedProp.EnumValues.Select(v => v.ToString()))})";
                }
                if (nestedProp.Minimum.HasValue) typeInfo += $" (min: {nestedProp.Minimum.Value})";
                if (nestedProp.Maximum.HasValue) typeInfo += $" (max: {nestedProp.Maximum.Value})";
                if (nestedProp.MinLength.HasValue) typeInfo += $" (minLen: {nestedProp.MinLength.Value})";
                if (nestedProp.MaxLength.HasValue) typeInfo += $" (maxLen: {nestedProp.MaxLength.Value})";
                if (!string.IsNullOrEmpty(nestedProp.Pattern)) typeInfo += $" (pattern: {nestedProp.Pattern})";

                var requiredStr = required ? "REQUIRED" : "(optional)";
                sb.AppendLine($"{currentPath} : [{typeInfo}] {nestedProp.Description ?? ""} {requiredStr}");

                // Recurse for nested objects
                if (nestedProp.Type?.ToLowerInvariant() == "object")
                {
                    AppendSchemaDescription(sb, nestedProp, currentPath, depth + 1);
                }
            }
        }
        else if (prop.Type?.ToLowerInvariant() == "array" && prop.Items != null)
        {
            // Describe the array item type
            var itemTypeInfo = prop.Items.Type ?? "unknown";
            if (prop.Items.EnumValues != null && prop.Items.EnumValues.Count > 0)
            {
                itemTypeInfo += $" (enum: {string.Join(", ", prop.Items.EnumValues.Select(v => v.ToString()))})";
            }
            // ... potentially add minItems, maxItems if schema defines them ...
            sb.AppendLine($"{parentPath}[] : [array of {itemTypeInfo}] {prop.Description ?? ""}");

            // If the item itself is an object, recurse
            if (prop.Items.Type?.ToLowerInvariant() == "object")
            {
                var itemPath = $"{parentPath}[]_item"; // Represent the item placeholder
                AppendSchemaDescription(sb, prop.Items, itemPath, depth + 1);
            }
        }
    }

    /// <summary>
    /// Генерирует объект для передачи в MCP с заполненными данными для простого примера
    /// </summary>
    public static object? GenerateExample(JsonSchemaProperty? schemaProperty, int depth = 0)
    {
        if (schemaProperty == null || depth > _maxDepth)
        {
            return new Dictionary<string, object?>();
        }

        return schemaProperty.Type?.ToLowerInvariant() switch
        {
            "string" => GetEnumOrDefault(schemaProperty, "sample_string"),
            "number" => GetEnumOrDefault(schemaProperty, 42.1),
            "integer" => GetEnumOrDefault(schemaProperty, 42),
            "boolean" => GetEnumOrDefault(schemaProperty, true),
            "array" => new List<object?> { GenerateExample(schemaProperty.Items, depth + 1) },
            "object" => GenerateObjectExample(schemaProperty, depth),
            _ => "unknown_type"
        };
    }

    private static object? GetEnumOrDefault(JsonSchemaProperty schemaProperty, object defaultValue)
    {
        return schemaProperty.EnumValues is { Count: > 0 }
            ? $"One of strings: {string.Join(',', schemaProperty.EnumValues)}"
            : defaultValue;
    }

    private static Dictionary<string, object?> GenerateObjectExample(JsonSchemaProperty schemaProperty, int depth = 0)
    {
        var obj = new Dictionary<string, object?>();
        if (schemaProperty.Properties != null)
        {
            foreach (var (key, value) in schemaProperty.Properties)
            {
                obj[key] = GenerateExample(value, depth + 1);
            }
        }
        return obj;
    }

    /// <summary>
    /// Validates and converts arguments based on the schema definition.
    /// </summary>
    /// <param name="schemaProperty">The root schema property definition.</param>
    /// <param name="inputArgs">The input arguments dictionary.</param>
    /// <param name="currentDepth">The current recursion depth (used internally).</param>
    /// <param name="maxDepth">The maximum allowed nesting depth.</param>
    /// <returns>A dictionary with validated and correctly typed arguments.</returns>
    public static Dictionary<string, object> ValidateAndConvertArguments(JsonSchemaProperty? schemaProperty, IReadOnlyDictionary<string, object> inputArgs, int currentDepth = 0, int maxDepth = _maxDepth)
    {
        var result = new Dictionary<string, object>();

        if (schemaProperty?.Properties == null || currentDepth > maxDepth)
        {
            // If no properties defined or max depth reached, pass through original args or return empty
            // For MCP, we likely want to pass through only known valid keys if possible, otherwise just return input ones if schema is flat at this level.
            // Returning empty here if no properties are defined might be safer depending on use case.
            // Let's assume if properties are null, we cannot validate further and return empty.
            return result;
        }

        foreach (var propDefKvp in schemaProperty.Properties)
        {
            var argName = propDefKvp.Key;
            var propDef = propDefKvp.Value;

            if (inputArgs.TryGetValue(argName, out var inputValue))
            {
                result[argName] = ConvertValueBySchema(inputValue, propDef, currentDepth, maxDepth);
            }
        }

        return result;
    }

    private static object ConvertValueBySchema(object inputValue, JsonSchemaProperty propDef, int currentDepth, int maxDepth)
    {
        if (currentDepth > maxDepth)
        {
            return inputValue; // Return as is if max depth hit
        }

        var expectedType = propDef.Type?.ToLowerInvariant();
        var convertedValue = expectedType switch
        {
            "integer" => TryConvertTo<int>(inputValue, out var intVal) ? (object?)intVal ?? inputValue : inputValue,
            "number" => TryConvertTo<double>(inputValue, out var doubleVal) ? (object?)doubleVal ?? inputValue : inputValue,
            "boolean" => TryConvertTo<bool>(inputValue, out var boolVal) ? (object?)boolVal ?? inputValue : inputValue,
            "string" => inputValue?.ToString() ?? string.Empty,
            "array" => ConvertArrayValue(inputValue, propDef.Items, currentDepth, maxDepth),
            "object" => ConvertObjectValue(inputValue, propDef, currentDepth, maxDepth),
            _ => inputValue // Default: return as is if type is unknown or null
        };

        return convertedValue;
    }

    private static bool TryConvertTo<T>(object value, out T? result) where T : struct
    {
        result = null;
        try
        {
            if (value is T directValue)
            {
                result = directValue;
                return true;
            }
            else if (value is string stringValue)
            {
                if (typeof(T) == typeof(int) && int.TryParse(stringValue, out var intParsed))
                {
                    result = (T)(object)intParsed;
                    return true;
                }
                else if (typeof(T) == typeof(double) && double.TryParse(stringValue, out var doubleParsed))
                {
                    result = (T)(object)doubleParsed;
                    return true;
                }
                else if (typeof(T) == typeof(bool) && bool.TryParse(stringValue, out var boolParsed))
                {
                    result = (T)(object)boolParsed;
                    return true;
                }
            }
        }
        catch
        {
            // Parsing failed
        }
        return false;
    }

    private static object ConvertArrayValue(object inputValue, JsonSchemaProperty? itemSchema, int currentDepth, int maxDepth)
    {
        if (inputValue is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<object>();
            foreach (var item in jsonEl.EnumerateArray())
            {
                var convertedItem = itemSchema != null ? ConvertValueBySchema(item, itemSchema, currentDepth + 1, maxDepth) : item;
                list.Add(convertedItem);
            }
            return list;
        }
        else if (inputValue is IEnumerable enumerable && !(inputValue is string))
        {
            var list = new List<object>();
            foreach (var item in enumerable)
            {
                var convertedItem = itemSchema != null ? ConvertValueBySchema(item!, itemSchema, currentDepth + 1, maxDepth) : item!;
                list.Add(convertedItem);
            }
            return list;
        }
        else if (inputValue is string inputValueStr)
        {
            var trimmed = inputValueStr.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                try
                {
                    var deInputValueStr = JsonSerializer.Deserialize<IEnumerable<object>>(trimmed) ?? [];
                    var list = new List<object>();
                    foreach (var item in deInputValueStr)
                    {
                        var convertedItem = itemSchema != null ? ConvertValueBySchema(item!, itemSchema, currentDepth + 1, maxDepth) : item!;
                        list.Add(convertedItem);
                    }
                    return list;
                }
                catch
                {
                    return inputValue;
                }
            }
        }
        // Fallback: return as is if conversion fails
        return inputValue;
    }

    private static object ConvertObjectValue(object inputValue, JsonSchemaProperty propDef, int currentDepth, int maxDepth)
    {
        // Assuming inputValue is a dictionary-like object (e.g., Dictionary<string, object>, JsonElement object)
        if (inputValue is Dictionary<string, object> dict)
        {
            return ValidateAndConvertArguments(propDef, dict, currentDepth + 1, maxDepth);
        }
        else if (inputValue is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.Object)
        {
            var tempDict = new Dictionary<string, object>();
            foreach (var prop in jsonEl.EnumerateObject())
            {
                tempDict[prop.Name] = prop.Value; // Pass raw JsonElement down for conversion
            }
            return ValidateAndConvertArguments(propDef, tempDict, currentDepth + 1, maxDepth);
        }
        // Fallback: return as is if conversion fails
        return inputValue;
    }
}
