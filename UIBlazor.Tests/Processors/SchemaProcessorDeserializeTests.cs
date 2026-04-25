using UIBlazor.Processors;

namespace UIBlazor.Tests.Processors;

public partial class SchemaProcessorTests
{
    #region Null and Invalid Element Tests

    [Fact]
    public void DeserializeSchema_NullElement_ReturnsNull()
    {
        JsonElement? nullElement = null;
        var result = SchemaProcessor.DeserializeSchema(nullElement);
        Assert.Null(result);
    }

    [Fact]
    public void DeserializeSchema_NonObjectElement_ReturnsNull()
    {
        var json = JsonDocument.Parse("\"string\"").RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.Null(result);
    }

    [Fact]
    public void DeserializeSchema_ArrayElement_ReturnsNull()
    {
        var json = JsonDocument.Parse("[1, 2, 3]").RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.Null(result);
    }

    [Fact]
    public void DeserializeSchema_NumberElement_ReturnsNull()
    {
        var json = JsonDocument.Parse("123.45").RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.Null(result);
    }

    [Fact]
    public void DeserializeSchema_BooleanElement_ReturnsNull()
    {
        var json = JsonDocument.Parse("true").RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.Null(result);
    }

    [Fact]
    public void DeserializeSchema_NullValueElement_ReturnsNull()
    {
        var json = JsonDocument.Parse("null").RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.Null(result);
    }

    #endregion

    #region Basic Properties Tests

    [Fact]
    public void DeserializeSchema_EmptyObject_ReturnsValidProperty()
    {
        var json = JsonDocument.Parse("{}").RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.Equal("object", result.Type); // Default value
    }

    [Fact]
    public void DeserializeSchema_BasicProperties_ExtractsCorrectly()
    {
        var json = JsonDocument.Parse("""
        {
            "type": "string",
            "description": "Test description"
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.Equal("string", result.Type);
        Assert.Equal("Test description", result.Description);
    }

    #endregion

    #region Type Extraction Tests

    [Theory]
    [InlineData("string")]
    [InlineData("number")]
    [InlineData("integer")]
    [InlineData("boolean")]
    [InlineData("array")]
    [InlineData("object")]
    public void DeserializeSchema_AllTypes_ExtractsCorrectly(string type)
    {
        var json = JsonDocument.Parse($"{{\"type\": \"{type}\"}}").RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.Equal(type, result.Type);
    }

    [Theory]
    [InlineData("STRING")]
    [InlineData("String")]
    [InlineData("NUMBER")]
    [InlineData("Boolean")]
    public void DeserializeSchema_TypesCaseInsensitive(string type)
    {
        var json = JsonDocument.Parse($"{{\"type\": \"{type}\"}}").RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.Equal(type, result.Type);
    }

    #endregion

    #region Enum Tests

    [Fact]
    public void DeserializeSchema_EnumValues_ExtractsCorrectly()
    {
        var json = JsonDocument.Parse("""
        {
            "type": "string",
            "enum": ["red", "green", "blue"]
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.NotNull(result.EnumValues);
        Assert.Equal(3, result.EnumValues.Count);
        Assert.Contains("red", result.EnumValues[0].ToString());
    }

    [Fact]
    public void DeserializeSchema_EnumWithMixedTypes_ExtractsCorrectly()
    {
        var json = JsonDocument.Parse("""
        {
            "enum": [1, "two", true, null]
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.NotNull(result.EnumValues);
        Assert.Equal(4, result.EnumValues.Count);
    }

    [Fact]
    public void DeserializeSchema_EnumWithNumbers_ExtractsCorrectly()
    {
        var json = JsonDocument.Parse("""
        {
            "enum": [1, 2, 3]
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.NotNull(result.EnumValues);
        Assert.Equal(3, result.EnumValues.Count);
    }

    [Fact]
    public void DeserializeSchema_EnumWithBooleans_ExtractsCorrectly()
    {
        var json = JsonDocument.Parse("""
        {
            "enum": [true, false]
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.NotNull(result.EnumValues);
        Assert.Equal(2, result.EnumValues.Count);
    }

    [Fact]
    public void DeserializeSchema_EnumNotArray_IgnoresEnum()
    {
        var json = JsonDocument.Parse("""
        {
            "enum": "not an array"
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.Null(result.EnumValues);
    }

    [Fact]
    public void DeserializeSchema_EnumAsNumber_IgnoresEnum()
    {
        var json = JsonDocument.Parse("""
        {
            "enum": 123
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.Null(result.EnumValues);
    }

    #endregion

    #region Constraint Tests

    [Fact]
    public void DeserializeSchema_Constraints_ExtractsCorrectly()
    {
        var json = JsonDocument.Parse("""
        {
            "type": "number",
            "minimum": 0.5,
            "maximum": 100.5,
            "minLength": 1,
            "maxLength": 255,
            "pattern": "^[a-z]+$"
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.Equal(0.5, result.Minimum);
        Assert.Equal(100.5, result.Maximum);
        Assert.Equal(1, result.MinLength);
        Assert.Equal(255, result.MaxLength);
        Assert.Equal("^[a-z]+$", result.Pattern);
    }

    [Theory]
    [InlineData("minimum", 0.5)]
    [InlineData("maximum", 100.5)]
    [InlineData("minLength", 1)]
    [InlineData("maxLength", 255)]
    public void DeserializeSchema_IndividualConstraint_ExtractsCorrectly(string constraint, object expected)
    {
        var json = JsonDocument.Parse($"{{\"type\": \"number\", \"{constraint}\": {expected}}}").RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        switch (constraint)
        {
            case "minimum": Assert.Equal(0.5, result.Minimum); break;
            case "maximum": Assert.Equal(100.5, result.Maximum); break;
            case "minLength": Assert.Equal(1, result.MinLength); break;
            case "maxLength": Assert.Equal(255, result.MaxLength); break;
        }
    }

    #endregion

    #region Required Tests

    [Fact]
    public void DeserializeSchema_Required_ExtractsCorrectly()
    {
        var json = JsonDocument.Parse("""
        {
            "type": "object",
            "required": ["name", "age"]
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.NotNull(result.Required);
        Assert.Contains("name", result.Required);
        Assert.Contains("age", result.Required);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"a\"]")]
    [InlineData("[\"a\", \"b\", \"c\"]")]
    public void DeserializeSchema_RequiredVariants_ExtractsCorrectly(string requiredJson)
    {
        var json = JsonDocument.Parse($"{{\"type\": \"object\", \"required\": {requiredJson}}}").RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.NotNull(result.Required);
    }

    [Fact]
    public void DeserializeSchema_RequiredNotArray_IgnoresRequired()
    {
        var json = JsonDocument.Parse("""
        {
            "required": "not an array"
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.Empty(result.Required);
    }

    [Fact]
    public void DeserializeSchema_RequiredAsObject_IgnoresRequired()
    {
        var json = JsonDocument.Parse("""
        {
            "required": {"name": true}
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.Empty(result.Required);
    }

    #endregion

    #region Nested Structure Tests

    [Fact]
    public void DeserializeSchema_NestedObject_ExtractsCorrectly()
    {
        var json = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "name": { "type": "string" },
                "address": {
                    "type": "object",
                    "properties": {
                        "city": { "type": "string" }
                    }
                }
            }
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.NotNull(result.Properties);
        Assert.Equal(2, result.Properties.Count);
        Assert.Equal("string", result.Properties["name"].Type);
        Assert.NotNull(result.Properties["address"]);
        Assert.NotNull(result.Properties["address"].Properties);
        Assert.Equal("string", result.Properties["address"].Properties["city"].Type);
    }

    [Fact]
    public void DeserializeSchema_ArrayWithItems_ExtractsCorrectly()
    {
        var json = JsonDocument.Parse("""
        {
            "type": "array",
            "items": {
                "type": "string"
            }
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.Equal("string", result.Items.Type);
    }

    [Fact]
    public void DeserializeSchema_NestedArray_ExtractsCorrectly()
    {
        var json = JsonDocument.Parse("""
        {
            "type": "array",
            "items": {
                "type": "array",
                "items": { "type": "number" }
            }
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.Equal("array", result.Items.Type);
        Assert.NotNull(result.Items.Items);
        Assert.Equal("number", result.Items.Items.Type);
    }

    [Fact]
    public void DeserializeSchema_ItemsNotObject_IgnoresItems()
    {
        var json = JsonDocument.Parse("""
        {
            "type": "array",
            "items": "string"
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.Null(result.Items);
    }

    #endregion

    #region Properties Validation Tests

    [Fact]
    public void DeserializeSchema_PropertiesNotObject_IgnoresProperties()
    {
        var json = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": "not an object"
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.Null(result.Properties);
    }

    [Fact]
    public void DeserializeSchema_PropertiesAsArray_IgnoresProperties()
    {
        var json = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": [1, 2, 3]
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.Null(result.Properties);
    }

    #endregion

    #region Depth Limit Tests

    [Fact]
    public void DeserializeSchema_MaxDepthExceeded_ReturnsNull()
    {
        var json = JsonDocument.Parse("{\"type\": \"object\", \"properties\": {}}").RootElement;
        var result = SchemaProcessor.DeserializeSchema(json, 6, 5);
        Assert.Null(result);
    }

    [Fact]
    public void DeserializeSchema_CustomMaxDepth_Respected()
    {
        var json = JsonDocument.Parse("{\"type\": \"object\", \"properties\": {}}").RootElement;
        var result = SchemaProcessor.DeserializeSchema(json, 3, 2);
        Assert.Null(result);
    }

    [Fact]
    public void DeserializeSchema_ExactMaxDepth_Succeeds()
    {
        var json = JsonDocument.Parse("{\"type\": \"object\", \"properties\": {}}").RootElement;
        var result = SchemaProcessor.DeserializeSchema(json, 3, 3);
        Assert.NotNull(result);
    }

    #endregion

    #region Complex Schema Tests

    [Fact]
    public void DeserializeSchema_ComplexSchema_ExtractsAllFields()
    {
        var json = JsonDocument.Parse("""
        {
            "type": "object",
            "description": "A complex user object",
            "properties": {
                "id": {
                    "type": "integer",
                    "description": "User ID"
                },
                "name": {
                    "type": "string",
                    "minLength": 1,
                    "maxLength": 100
                },
                "email": {
                    "type": "string",
                    "pattern": "^[^@]+@[^@]+$"
                },
                "roles": {
                    "type": "array",
                    "items": {
                        "type": "string",
                        "enum": ["admin", "user", "guest"]
                    }
                },
                "metadata": {
                    "type": "object",
                    "properties": {
                        "created": {"type": "string"},
                        "updated": {"type": "string"}
                    }
                }
            },
            "required": ["id", "name"]
        }
        """).RootElement;
        var result = SchemaProcessor.DeserializeSchema(json);
        Assert.NotNull(result);
        Assert.Equal("object", result.Type);
        Assert.Equal("A complex user object", result.Description);
        Assert.NotNull(result.Properties);
        Assert.Equal(5, result.Properties.Count);
        Assert.Equal(2, result.Required.Count);
        Assert.Equal("integer", result.Properties["id"].Type);
        Assert.Equal("User ID", result.Properties["id"].Description);
        Assert.Equal("string", result.Properties["name"].Type);
        Assert.Equal(1, result.Properties["name"].MinLength);
        Assert.Equal(100, result.Properties["name"].MaxLength);
        Assert.Equal("^[^@]+@[^@]+$", result.Properties["email"].Pattern);
        Assert.Equal("array", result.Properties["roles"].Type);
        Assert.NotNull(result.Properties["roles"].Items);
        Assert.NotNull(result.Properties["roles"].Items.EnumValues);
        Assert.NotNull(result.Properties["metadata"].Properties);
        Assert.Equal(2, result.Properties["metadata"].Properties.Count);
    }

    #endregion
}
