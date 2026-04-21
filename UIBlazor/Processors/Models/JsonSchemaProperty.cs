namespace UIBlazor.Processors.Models;

/// <summary>
/// Represents a property definition within a JSON Schema.
/// Supports recursive structures for nested objects and arrays.
/// </summary>
public class JsonSchemaProperty
{
    public string? Type { get; set; } = "object";
    public string? Description { get; set; }

    // Constraints
    public List<object>? EnumValues { get; set; }
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public string? Pattern { get; set; }

    // Nested structures
    public Dictionary<string, JsonSchemaProperty>? Properties { get; set; }

    public JsonSchemaProperty? Items { get; set; }

    public List<string> Required { get; set; } = [];
}
