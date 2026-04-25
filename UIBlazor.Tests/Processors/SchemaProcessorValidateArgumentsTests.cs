using UIBlazor.Processors;
using UIBlazor.Processors.Models;

namespace UIBlazor.Tests.Processors;

public partial class SchemaProcessorTests
{
    #region Null and Empty Tests

    [Fact]
    public void ValidateAndConvertArguments_NullSchema_ReturnsEmptyDictionary()
    {
        var args = new Dictionary<string, object> { ["key"] = "value" };
        var result = SchemaProcessor.ValidateAndConvertArguments(null, args);
        Assert.Empty(result);
    }

    [Fact]
    public void ValidateAndConvertArguments_NullProperties_ReturnsEmptyDictionary()
    {
        var schema = new JsonSchemaProperty();
        var args = new Dictionary<string, object> { ["key"] = "value" };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Empty(result);
    }

    [Fact]
    public void ValidateAndConvertArguments_EmptyArgs_ReturnsEmptyResult()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["name"] = new JsonSchemaProperty { Type = "string" }
            }
        };
        var args = new Dictionary<string, object>();
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Empty(result);
    }

    [Fact]
    public void ValidateAndConvertArguments_EmptyPropertiesSchema_ReturnsEmpty()
    {
        var schema = new JsonSchemaProperty
        {
            Type = "object",
            Properties = new Dictionary<string, JsonSchemaProperty>()
        };
        var args = new Dictionary<string, object> { ["any"] = "value" };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Empty(result);
    }

    #endregion

    #region Depth Tests

    [Fact]
    public void ValidateAndConvertArguments_MaxDepthExceeded_ReturnsEmptyDictionary()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["prop"] = new JsonSchemaProperty { Type = "string" }
            }
        };
        var args = new Dictionary<string, object> { ["prop"] = "value" };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args, 10, 5);
        Assert.Empty(result);
    }

    [Fact]
    public void ValidateAndConvertArguments_DeeplyNestedObject_RespectsMaxDepth()
    {
        var schema = BuildDeepSchema(3);
        var args = BuildDeepArgs(3);
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args, 0, 2);
        Assert.NotEmpty(result);
    }

    #endregion

    #region Missing and Extra Args Tests

    [Fact]
    public void ValidateAndConvertArguments_MissingArg_NotIncludedInResult()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["name"] = new JsonSchemaProperty { Type = "string" },
                ["age"] = new JsonSchemaProperty { Type = "integer" }
            }
        };
        var args = new Dictionary<string, object> { ["name"] = "John" };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Single(result);
        Assert.Contains("name", result);
        Assert.DoesNotContain("age", result);
    }

    [Fact]
    public void ValidateAndConvertArguments_ExtraArgs_IgnoresExtra()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["name"] = new JsonSchemaProperty { Type = "string" }
            }
        };
        var args = new Dictionary<string, object>
        {
            ["name"] = "John",
            ["extra"] = "ignored"
        };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Single(result);
        Assert.DoesNotContain("extra", result.Keys);
    }

    [Fact]
    public void ValidateAndConvertArguments_NullValueInArgs_HandledGracefully()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["name"] = new JsonSchemaProperty { Type = "string" }
            }
        };
        var args = new Dictionary<string, object> { ["name"] = null! };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Contains("name", result.Keys);
    }

    #endregion

    #region String Type Tests

    [Theory]
    [InlineData("John")]
    [InlineData("")]
    [InlineData("Special: Chars @#$%")]
    public void ValidateAndConvertArguments_StringValue_PreservesValue(string value)
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["name"] = new JsonSchemaProperty { Type = "string" }
            }
        };
        var args = new Dictionary<string, object> { ["name"] = value };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Equal(value, result["name"]);
    }

    #endregion

    #region Integer Type Tests

    [Fact]
    public void ValidateAndConvertArguments_IntegerFromNumber_PreservesValue()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["count"] = new JsonSchemaProperty { Type = "integer" }
            }
        };
        var args = new Dictionary<string, object> { ["count"] = 42 };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Equal(42, result["count"]);
    }

    [Fact]
    public void ValidateAndConvertArguments_IntegerFromString_ParsesValue()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["count"] = new JsonSchemaProperty { Type = "integer" }
            }
        };
        var args = new Dictionary<string, object> { ["count"] = "123" };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Equal(123, result["count"]);
    }

    [Theory]
    [InlineData("not a number")]
    [InlineData("12.34")]
    [InlineData("")]
    [InlineData("abc123")]
    public void ValidateAndConvertArguments_IntegerFromInvalidString_KeepsOriginal(string invalidValue)
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["count"] = new JsonSchemaProperty { Type = "integer" }
            }
        };
        var args = new Dictionary<string, object> { ["count"] = invalidValue };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Equal(invalidValue, result["count"]);
    }

    #endregion

    #region Number Type Tests

    [Fact]
    public void ValidateAndConvertArguments_NumberFromDouble_PreservesValue()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["price"] = new JsonSchemaProperty { Type = "number" }
            }
        };
        var args = new Dictionary<string, object> { ["price"] = 99.99 };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Equal(99.99, result["price"]);
    }

    [Fact]
    public void ValidateAndConvertArguments_NumberFromString_ParsesValue()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["price"] = new JsonSchemaProperty { Type = "number" }
            }
        };
        var args = new Dictionary<string, object> { ["price"] = "45.5" };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Equal(45.5, result["price"]);
    }

    [Theory]
    [InlineData("not a decimal")]
    [InlineData("abc")]
    [InlineData("")]
    public void ValidateAndConvertArguments_NumberFromInvalidString_KeepsOriginal(string invalidValue)
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["price"] = new JsonSchemaProperty { Type = "number" }
            }
        };
        var args = new Dictionary<string, object> { ["price"] = invalidValue };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Equal(invalidValue, result["price"]);
    }

    #endregion

    #region Boolean Type Tests

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateAndConvertArguments_BooleanFromBool_PreservesValue(bool value)
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["active"] = new JsonSchemaProperty { Type = "boolean" }
            }
        };
        var args = new Dictionary<string, object> { ["active"] = value };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Equal(value, result["active"]);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    [InlineData("FALSE", false)]
    [InlineData("truE", true)]
    [InlineData("FaLsE", false)]
    public void ValidateAndConvertArguments_BooleanFromString_ConvertsValue(string input, bool expected)
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["active"] = new JsonSchemaProperty { Type = "boolean" }
            }
        };
        var args = new Dictionary<string, object> { ["active"] = input };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Equal(expected, result["active"]);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("maybe")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("2")]
    public void ValidateAndConvertArguments_BooleanFromInvalidString_KeepsOriginal(string invalidValue)
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["active"] = new JsonSchemaProperty { Type = "boolean" }
            }
        };
        var args = new Dictionary<string, object> { ["active"] = invalidValue };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Equal(invalidValue, result["active"]);
    }

    #endregion

    #region Array Type Tests

    [Fact]
    public void ValidateAndConvertArguments_ArrayFromJsonElement_ConvertsCorrectly()
    {
        var jsonArray = JsonDocument.Parse("[1, 2, 3]").RootElement;
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["items"] = new JsonSchemaProperty
                {
                    Type = "array",
                    Items = new JsonSchemaProperty { Type = "integer" }
                }
            }
        };
        var args = new Dictionary<string, object> { ["items"] = jsonArray };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.IsType<List<object>>(result["items"]);
        var list = (List<object>)result["items"];
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void ValidateAndConvertArguments_ArrayFromString_DeserializesAndConverts()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["items"] = new JsonSchemaProperty
                {
                    Type = "array",
                    Items = new JsonSchemaProperty { Type = "integer" }
                }
            }
        };
        var args = new Dictionary<string, object> { ["items"] = "[1, 2, 3]" };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.IsType<List<object>>(result["items"]);
    }

    [Fact]
    public void ValidateAndConvertArguments_ArrayFromInvalidString_KeepsOriginal()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["items"] = new JsonSchemaProperty { Type = "array" }
            }
        };
        var args = new Dictionary<string, object> { ["items"] = "not a valid array string" };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Equal("not a valid array string", result["items"]);
    }

    [Fact]
    public void ValidateAndConvertArguments_ArrayFromList_PreservesList()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["items"] = new JsonSchemaProperty { Type = "array" }
            }
        };
        var list = new List<object> { "a", "b", "c" };
        var args = new Dictionary<string, object> { ["items"] = list };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.IsType<List<object>>(result["items"]);
    }

    [Fact]
    public void ValidateAndConvertArguments_ArrayFromListWithWhitespace_DeserializesCorrectly()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["items"] = new JsonSchemaProperty { Type = "array" }
            }
        };
        var args = new Dictionary<string, object> { ["items"] = "  [1, 2, 3]  " };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.IsType<List<object>>(result["items"]);
    }

    #endregion

    #region Object Type Tests

    [Fact]
    public void ValidateAndConvertArguments_ObjectFromDictionary_ValidatesNested()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["user"] = new JsonSchemaProperty
                {
                    Type = "object",
                    Properties = new Dictionary<string, JsonSchemaProperty>
                    {
                        ["name"] = new JsonSchemaProperty { Type = "string" },
                        ["age"] = new JsonSchemaProperty { Type = "integer" }
                    }
                }
            }
        };
        var args = new Dictionary<string, object>
        {
            ["user"] = new Dictionary<string, object>
            {
                ["name"] = "John",
                ["age"] = "30"
            }
        };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.IsType<Dictionary<string, object>>(result["user"]);
        var user = (Dictionary<string, object>)result["user"];
        Assert.Equal("John", user["name"]);
        Assert.Equal(30, user["age"]);
    }

    [Fact]
    public void ValidateAndConvertArguments_ObjectFromJsonElement_ValidatesNested()
    {
        var jsonObj = JsonDocument.Parse("{\"name\": \"Jane\", \"age\": \"25\"}").RootElement;
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["user"] = new JsonSchemaProperty
                {
                    Type = "object",
                    Properties = new Dictionary<string, JsonSchemaProperty>
                    {
                        ["name"] = new JsonSchemaProperty { Type = "string" },
                        ["age"] = new JsonSchemaProperty { Type = "integer" }
                    }
                }
            }
        };
        var args = new Dictionary<string, object> { ["user"] = jsonObj };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.IsType<Dictionary<string, object>>(result["user"]);
    }

    #endregion

    #region Unknown Type Tests

    [Fact]
    public void ValidateAndConvertArguments_UnknownType_KeepsOriginalValue()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["data"] = new JsonSchemaProperty { Type = "unknown" }
            }
        };
        var args = new Dictionary<string, object> { ["data"] = "some value" };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Equal("some value", result["data"]);
    }

    [Fact]
    public void ValidateAndConvertArguments_NullType_KeepsOriginalValue()
    {
        var schema = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["data"] = new JsonSchemaProperty { Type = null }
            }
        };
        var args = new Dictionary<string, object> { ["data"] = "some value" };
        var result = SchemaProcessor.ValidateAndConvertArguments(schema, args);
        Assert.Equal("some value", result["data"]);
    }

    #endregion

    #region Helper Methods

    private static JsonSchemaProperty BuildDeepSchema(int depth)
    {
        if (depth == 0)
        {
            return new JsonSchemaProperty
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchemaProperty>
                {
                    ["name"] = new JsonSchemaProperty { Type = "string" }
                }
            };
        }

        return new JsonSchemaProperty
        {
            Type = "object",
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["nested"] = BuildDeepSchema(depth - 1)
            }
        };
    }

    private static Dictionary<string, object> BuildDeepArgs(int depth)
    {
        if (depth == 0)
        {
            return new Dictionary<string, object> { ["name"] = "test" };
        }

        return new Dictionary<string, object>
        {
            ["nested"] = BuildDeepArgs(depth - 1)
        };
    }

    #endregion
}
