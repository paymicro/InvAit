using System.Text.Json.Nodes;
using Radzen.Blazor.Markdown;

namespace UIBlazor.Processors;

public static class SchemaProcessor
{
    private const int _defaultMaxDepth = 5;

    /// <summary>
    /// Deserializes a JsonElement representing a JSON Schema into a JsonSchemaProperty object graph.
    /// </summary>
    /// <param name="schemaElement">The JsonElement containing the schema.</param>
    /// <param name="currentDepth">The current recursion depth (used internally).</param>
    /// <param name="maxDepth">The maximum allowed nesting depth.</param>
    /// <returns>A JsonSchemaProperty object representing the schema, or null if the input is invalid.</returns>
    public static JsonSchemaProperty? DeserializeSchema(JsonElement? schemaElement, int currentDepth = 0, int maxDepth = _defaultMaxDepth)
    {
        if (!schemaElement.HasValue || schemaElement.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (currentDepth > maxDepth)
        {
            // Log a warning or throw an exception if depth is exceeded
            Console.WriteLine($"Warning: Maximum schema depth ({maxDepth}) exceeded during deserialization.");
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

    //public static Dictionary<string,  GenerateToolParameter(JsonSchemaProperty prop)
    //{

    //}

    public static void AppendSchemaDescription(StringBuilder sb, JsonSchemaProperty prop, string parentPath, int depth = 0)
    {
        if (prop.Properties != null)
        {
            foreach (var nestedPropKvp in prop.Properties)
            {
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

                sb.AppendLine($"{currentPath} : [{typeInfo}] {nestedProp.Description ?? ""}");

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
    /// Generates an example value based on the schema definition.
    /// </summary>
    /// <param name="schemaProperty">The schema property definition.</param>
    /// <param name="currentDepth">The current recursion depth (used internally).</param>
    /// <param name="maxDepth">The maximum allowed nesting depth.</param>
    /// <returns>An example value matching the schema.</returns>
    public static object GenerateExample(JsonSchemaProperty? schemaProperty, int currentDepth = 0, int maxDepth = _defaultMaxDepth)
    {
        if (schemaProperty == null || currentDepth > maxDepth)
        {
            return new JsonObject(); // Fallback for null or max depth
        }

        return schemaProperty.Type?.ToLowerInvariant() switch
        {
            "string" => schemaProperty.EnumValues?.Count > 0 ? schemaProperty.EnumValues[0].ToString() ?? "enum_value" : "\"string\"",
            "number" => schemaProperty.EnumValues?.Count > 0 ? (double)schemaProperty.EnumValues[0] : 123.45,
            "integer" => schemaProperty.EnumValues?.Count > 0 ? Convert.ToInt32(schemaProperty.EnumValues[0]) : 123,
            "boolean" => schemaProperty.EnumValues?.Count > 0 ? Convert.ToBoolean(schemaProperty.EnumValues[0]) : true,
            "array" => GenerateExample(schemaProperty.Items, currentDepth + 1, maxDepth),
            "object" => GenerateObjectExample(schemaProperty, currentDepth, maxDepth),
            _ => "\"unknown_type_or_null\""
        };
    }

    private static JsonObject GenerateObjectExample(JsonSchemaProperty schemaProperty, int currentDepth, int maxDepth)
    {
        var obj = new JsonObject();
        if (schemaProperty.Properties != null)
        {
            foreach (var kvp in schemaProperty.Properties)
            {
                var exampleValue = GenerateExample(kvp.Value, currentDepth + 1, maxDepth);
                // Convert the returned object to JsonNode before adding to JsonObject
                if (exampleValue is JsonNode jsonNode)
                {
                    obj[kvp.Key] = jsonNode;
                }
                else
                {
                    // If it's a primitive, wrap it or serialize it
                    obj[kvp.Key] = JsonSerializer.SerializeToNode(exampleValue?.ToString());
                }
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
    public static Dictionary<string, object> ValidateAndConvertArguments(JsonSchemaProperty? schemaProperty, IReadOnlyDictionary<string, object> inputArgs, int currentDepth = 0, int maxDepth = _defaultMaxDepth)
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
            Console.WriteLine($"Warning: Maximum argument conversion depth ({maxDepth}) exceeded.");
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
        // Assuming inputValue is an array/object that can be enumerated or cast
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
        else if (inputValue is IEnumerable<object> enumerable)
        {
            var list = new List<object>();
            foreach (var item in enumerable)
            {
                var convertedItem = itemSchema != null ? ConvertValueBySchema(item, itemSchema, currentDepth + 1, maxDepth) : item;
                list.Add(convertedItem);
            }
            return list;
        }
        else if (inputValue is string inputValueStr && inputValueStr.StartsWith('[') && inputValueStr.EndsWith(']') )
        {
            try
            {
                var deInputValueStr = JsonSerializer.Deserialize<IEnumerable<object>>(inputValueStr) ?? [];
                var list = new List<object>();
                foreach (var item in deInputValueStr)
                {
                    var convertedItem = itemSchema != null ? ConvertValueBySchema(item, itemSchema, currentDepth + 1, maxDepth) : item;
                    list.Add(convertedItem);
                }
                return list;
            }
            catch
            {
                return inputValue;
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
