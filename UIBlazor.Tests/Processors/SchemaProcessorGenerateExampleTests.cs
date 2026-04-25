using UIBlazor.Processors;
using UIBlazor.Processors.Models;

namespace UIBlazor.Tests.Processors;

public partial class SchemaProcessorTests
{
    #region Null and Depth Tests

    [Fact]
    public void GenerateExample_NullSchema_ReturnsEmptyJsonObject()
    {
        var result = SchemaProcessor.GenerateExample(null);
        Assert.IsType<Dictionary<string, object>>(result);
    }

    [Fact]
    public void GenerateExample_MaxDepthExceeded_ReturnsEmptyJsonObject()
    {
        var schema = new JsonSchemaProperty { Type = "object" };
        var result = SchemaProcessor.GenerateExample(schema, 10);
        Assert.IsType<Dictionary<string, object>>(result);
    }

    [Fact]
    public void GenerateExample_DirectMaxDepth_ReturnsEmptyJsonObject()
    {
        var schema = new JsonSchemaProperty { Type = "object" };
        var result = SchemaProcessor.GenerateExample(schema, 5);
        Assert.IsType<Dictionary<string, object>>(result);
    }

    #endregion

    #region String Type Tests

    [Theory]
    [InlineData("string")]
    [InlineData("STRING")]
    [InlineData("String")]
    public void GenerateExample_StringType_ReturnsStringExample(string type)
    {
        var schema = new JsonSchemaProperty { Type = type };
        var result = SchemaProcessor.GenerateExample(schema);
        Assert.Equal("sample_string", result.ToString());
    }

    [Fact]
    public void GenerateExample_StringWithEnum_ReturnsFirstEnumValue()
    {
        var schema = new JsonSchemaProperty
        {
            Type = "string",
            EnumValues = new List<object> { JsonDocument.Parse("\"option1\"").RootElement, JsonDocument.Parse("\"option2\"").RootElement }
        };
        var result = SchemaProcessor.GenerateExample(schema);
        Assert.Contains("option1", result.ToString());
    }

    [Fact]
    public void GenerateExample_EmptyEnum_ReturnsDefault()
    {
        var schema = new JsonSchemaProperty
        {
            Type = "string",
            EnumValues = []
        };
        var result = SchemaProcessor.GenerateExample(schema);
        Assert.Equal("sample_string", result);
    }

    #endregion

    #region Number Type Tests

    [Theory]
    [InlineData("number")]
    [InlineData("NUMBER")]
    public void GenerateExample_NumberType_ReturnsNumberExample(string type)
    {
        var schema = new JsonSchemaProperty { Type = type };
        var result = SchemaProcessor.GenerateExample(schema);
        Assert.Equal(0.1, result);
    }

    [Theory]
    [InlineData(42.5)]
    [InlineData(100.0)]
    [InlineData(-3.14)]
    public void GenerateExample_NumberWithEnum_ReturnsFirstEnumValue(double enumValue)
    {
        var schema = new JsonSchemaProperty
        {
            Type = "number",
            EnumValues = new List<object> { JsonDocument.Parse(enumValue.ToString()).RootElement }
        };
        var result = SchemaProcessor.GenerateExample(schema);
        Assert.Equal($"One of strings: {enumValue}", result);
    }

    #endregion

    #region Integer Type Tests

    [Theory]
    [InlineData("integer")]
    [InlineData("INTEGER")]
    public void GenerateExample_IntegerType_ReturnsIntegerExample(string type)
    {
        var schema = new JsonSchemaProperty { Type = type };
        var result = SchemaProcessor.GenerateExample(schema);
        Assert.Equal(0, result);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(0)]
    [InlineData(-42)]
    public void GenerateExample_IntegerWithEnum_ReturnsFirstEnumValue(int enumValue)
    {
        var schema = new JsonSchemaProperty
        {
            Type = "integer",
            EnumValues = [JsonDocument.Parse(enumValue.ToString()).RootElement]
        };
        var result = SchemaProcessor.GenerateExample(schema);
        Assert.Equal($"One of strings: {enumValue}", result);
    }

    #endregion

    #region Boolean Type Tests

    [Theory]
    [InlineData("boolean")]
    [InlineData("BOOLEAN")]
    public void GenerateExample_BooleanType_ReturnsBooleanExample(string type)
    {
        var schema = new JsonSchemaProperty { Type = type };
        var result = SchemaProcessor.GenerateExample(schema);
        Assert.Equal(true, result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenerateExample_BooleanWithEnum_ReturnsFirstEnumValue(bool enumValue)
    {
        var schema = new JsonSchemaProperty
        {
            Type = "boolean",
            EnumValues = [enumValue]
        };
        var result = SchemaProcessor.GenerateExample(schema);
        Assert.Equivalent($"One of strings: {enumValue}", result);
    }

    #endregion
}
