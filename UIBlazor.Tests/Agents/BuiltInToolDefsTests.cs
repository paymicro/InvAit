using System.ComponentModel;
using Shared.Contracts;
using UIBlazor.Agents;
using UIBlazor.Utils;

namespace UIBlazor.Tests.Agents;

public class BuiltInToolDefsTests
{
    #region Test Helper Methods

    // Метод без параметров
    public void MethodWithoutParameters() { }

    // Метод с описанием
    [Description("Test method description")]
    public string MethodWithDescription(string param1)
    {
        return param1;
    }

    // Метод без описания
    public void MethodWithoutDescription(int param1) { }

    // Метод с параметром имеющим описание
    public void MethodWithParameterDescription(
        [Description("Parameter description")] string param1) { }

    // Метод с параметром по умолчанию
    public void MethodWithOptionalParameter(string required, int optional = 10) { }

    // Метод с различными примитивными типами
    public void MethodWithPrimitives(
        string stringParam,
        int intParam,
        long longParam,
        bool boolParam,
        double doubleParam,
        float floatParam,
        decimal decimalParam,
        DateTime dateTimeParam,
        Guid guidParam) { }

    // Метод с массивом
    public void MethodWithArray(string[] arrayParam) { }

    // Метод со списком
    public void MethodWithList(List<int> listParam) { }

    // Метод со сложным объектом
    public void MethodWithComplexObject(TestComplexObject objParam) { }

    // Метод с несколькими параметрами
    [Description("Multi parameter method")]
    public void MethodWithMultipleParameters(
        [Description("First parameter")] string param1,
        [Description("Second parameter")] int param2,
        [Description("Third parameter")] bool param3 = false) { }

    // Метод с nullable типом
    public void MethodWithNullableType(int? nullableInt, string? nullableString) { }

    // Метод с enum
    public void MethodWithEnum(TestEnum enumParam) { }

    #endregion

    #region Test Classes

    public class TestComplexObject
    {
        [Description("Name property")]
        public required string Name { get; set; }

        [Description("Count property")]
        public int Count { get; set; }

        public string NoDescriptionProp { get; set; } = "";
    }

    public enum TestEnum
    {
        Value1,
        Value2,
        Value3
    }

    #endregion

    #region Tests

    [Fact]
    public void MapMethodToTool_MethodWithoutParameters_ReturnsEmptyProperties()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithoutParameters))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert
        Assert.Equal("MethodWithoutParameters", result.Function.Name);
        Assert.Equal("", result.Function.Description);
        Assert.Equal("object", result.Function.Parameters.Type);
        Assert.Empty(result.Function.Parameters.Properties);
        Assert.Empty(result.Function.Parameters.Required);
    }

    [Fact]
    public void MapMethodToTool_MethodWithDescription_ReturnsCorrectDescription()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithDescription))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert
        Assert.Equal("MethodWithDescription", result.Function.Name);
        Assert.Equal("Test method description", result.Function.Description);
    }

    [Fact]
    public void MapMethodToTool_MethodWithoutDescription_ReturnsEmptyDescription()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithoutDescription))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert
        Assert.Equal("", result.Function.Description);
    }

    [Fact]
    public void MapMethodToTool_ParameterWithDescription_ReturnsCorrectPropertyDescription()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithParameterDescription))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert
        Assert.True(result.Function.Parameters.Properties.ContainsKey("param1"));
        Assert.Equal("Parameter description", result.Function.Parameters.Properties["param1"].Description);
    }

    [Fact]
    public void MapMethodToTool_ApplyDiff_ReturnsCorrectPropertyDescription()
    {
        // Arrange
        var method = typeof(BuiltInToolDefs).GetMethod(nameof(BuiltInToolDefs.ApplyDiff));

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);
        var json = JsonUtils.SerializeCompact(result);

        // Assert
        Assert.True(result.Function.Parameters.Properties.ContainsKey("filePath"));
        Assert.Equal("File path", result.Function.Parameters.Properties["filePath"].Description);
        Assert.Equivalent(json, "{\"type\":\"function\",\"function\":{\"name\":\"ApplyDiff\",\"description\":\"Applies a series of Search & Replace edits to the specified file.\",\"strict\":true,\"parameters\":{\"type\":\"object\",\"properties\":{\"filePath\":{\"type\":\"string\",\"description\":\"File path\"},\"edits\":{\"type\":\"array\",\"description\":\"List of pairs 'search/replace'. Executed sequentially.\",\"items\":{\"type\":\"object\",\"properties\":{\"approximateLine\":{\"type\":[\"integer\",\"null\"],\"description\":\"Approximate start line or null\"},\"oldStr\":{\"type\":\"string\",\"description\":\"Unique fragment of code\"},\"newStr\":{\"type\":\"string\",\"description\":\"New fragment of code\"}},\"required\":[\"approximateLine\",\"oldStr\",\"newStr\"],\"additionalProperties\":false}}},\"required\":[\"filePath\",\"edits\"],\"additionalProperties\":false}}}");
    }

    [Fact]
    public void MapMethodToTool_AllParametersInRequired_InStrictMode()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithOptionalParameter))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert - В Strict Mode ВСЕ параметры в required
        Assert.Equal(2, result.Function.Parameters.Required.Count);
        Assert.Contains("required", result.Function.Parameters.Required);
        Assert.Contains("optional", result.Function.Parameters.Required);
    }

    [Fact]
    public void MapMethodToTool_OptionalParameter_HasUnionTypeWithNull()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithOptionalParameter))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert - optional параметр имеет тип [integer, null]
        var optionalProp = result.Function.Parameters.Properties["optional"];
        Assert.True(optionalProp.IsUnionType);
    }

    [Fact]
    public void MapMethodToTool_RequiredParameter_HasSingleType()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithOptionalParameter))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert - required параметр имеет простой тип string
        var requiredProp = result.Function.Parameters.Properties["required"];
        Assert.False(requiredProp.IsUnionType);
        Assert.Equal("string", requiredProp.Type);
    }

    [Fact]
    public void MapMethodToTool_PrimitiveTypes_ReturnsCorrectTypes()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithPrimitives))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert
        var props = result.Function.Parameters.Properties;

        Assert.Equal("string", props["stringParam"].Type);
        Assert.Equal("integer", props["intParam"].Type);
        Assert.Equal("integer", props["longParam"].Type);
        Assert.Equal("boolean", props["boolParam"].Type);
        Assert.Equal("number", props["doubleParam"].Type);
        Assert.Equal("number", props["floatParam"].Type);
        Assert.Equal("number", props["decimalParam"].Type);
        Assert.Equal("string", props["dateTimeParam"].Type);
        Assert.Equal("string", props["guidParam"].Type);
    }

    [Fact]
    public void MapMethodToTool_ArrayType_ReturnsArrayWithItems()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithArray))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert
        var prop = result.Function.Parameters.Properties["arrayParam"];
        Assert.Equal("array", prop.Type);
        Assert.NotNull(prop.Items);
        Assert.Equal("{\"type\":\"string\"}", JsonUtils.SerializeCompact(prop.Items));
    }

    [Fact]
    public void MapMethodToTool_ListType_ReturnsArrayWithItems()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithList))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert
        var prop = result.Function.Parameters.Properties["listParam"];
        Assert.Equal("array", prop.Type);
        Assert.NotNull(prop.Items);
        Assert.Equal("{\"type\":\"integer\"}", JsonUtils.SerializeCompact(prop.Items));
    }

    [Fact]
    public void MapMethodToTool_ComplexObject_ReturnsObjectWithProperties()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithComplexObject))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert
        var prop = result.Function.Parameters.Properties["objParam"];
        Assert.Equal("object", prop.Type);
        Assert.NotNull(prop.Properties);
        Assert.True(prop.Properties.ContainsKey("name"));
        Assert.True(prop.Properties.ContainsKey("count"));
        Assert.True(prop.Properties.ContainsKey("noDescriptionProp"));

        Assert.Equal("Name property", prop.Properties["name"].Description);
        Assert.Equal("Count property", prop.Properties["count"].Description);
        Assert.Null(prop.Properties["noDescriptionProp"].Description);

        Assert.Equal("string", prop.Properties["name"].Type);
        Assert.Equal("integer", prop.Properties["count"].Type);
    }

    [Fact]
    public void MapMethodToTool_MultipleParameters_AllMappedCorrectly()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithMultipleParameters))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert
        Assert.Equal("Multi parameter method", result.Function.Description);
        Assert.Equal(3, result.Function.Parameters.Properties.Count);

        Assert.Equal("First parameter", result.Function.Parameters.Properties["param1"].Description);
        Assert.Equal("Second parameter", result.Function.Parameters.Properties["param2"].Description);
        Assert.Equal("Third parameter", result.Function.Parameters.Properties["param3"].Description);

        Assert.Equal("string", result.Function.Parameters.Properties["param1"].Type);
        Assert.Equal("integer", result.Function.Parameters.Properties["param2"].Type);
        // param3 имеет дефолтное значение, поэтому union type ["boolean", "null"]
        Assert.True(result.Function.Parameters.Properties["param3"].IsUnionType);

        // В Strict Mode все 3 параметра в required
        Assert.Equal(3, result.Function.Parameters.Required.Count);
        Assert.Contains("param1", result.Function.Parameters.Required);
        Assert.Contains("param2", result.Function.Parameters.Required);
        Assert.Contains("param3", result.Function.Parameters.Required);
        
        // param3 имеет дефолтное значение, поэтому union type
        Assert.True(result.Function.Parameters.Properties["param3"].IsUnionType);
    }

    [Fact]
    public void MapMethodToTool_ReturnsCorrectToolDefinitionStructure()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithDescription))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert
        Assert.Equal("function", result.Type);
        Assert.NotNull(result.Function);
        Assert.NotNull(result.Function.Parameters);
        Assert.NotNull(result.Function.Parameters.Properties);
        Assert.NotNull(result.Function.Parameters.Required);
    }

    [Fact]
    public void MapMethodToTool_NullableType_HasUnionTypeWithNull()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithNullableType))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert - nullable типы (int?) обрабатываются как union
        var nullableIntProp = result.Function.Parameters.Properties["nullableInt"];
        Assert.True(nullableIntProp.IsUnionType);

        // nullable string уже string, и параметр не имеет дефолтного значения
        // поэтому это просто string
        var nullableStringProp = result.Function.Parameters.Properties["nullableString"];
        Assert.Equal("string", nullableStringProp.Type);
    }

    [Fact]
    public void MapMethodToTool_EnumType_ReturnsStringWithValuesInDescription()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithEnum))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert
        var prop = result.Function.Parameters.Properties["enumParam"];
        Assert.Equal("string", prop.Type);
        Assert.Contains("Value1", prop.Description);
        Assert.Contains("Value2", prop.Description);
        Assert.Contains("Value3", prop.Description);
    }

    [Fact]
    public void MapMethodToTool_ParameterNamesInCamelCase()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithPrimitives))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert
        Assert.Contains("stringParam", result.Function.Parameters.Properties.Keys);
        Assert.Contains("intParam", result.Function.Parameters.Properties.Keys);
        Assert.Contains("dateTimeParam", result.Function.Parameters.Properties.Keys);
    }

    #endregion

    [Fact]
    public async Task InvokeToolAsync_ApplyDiff()
    {
        // Arrange
        var defs = new BuiltInToolDefs();
        var payload = new
        {
            filePath = "C:\\file.cs",
            edits = new List<DiffEdit>()
            {
                { new DiffEdit { ApproximateLine = 1, OldStr = "old", NewStr = "new" } }
            }
        };

        // Act
        var result = await defs.InvokeToolAsync(nameof(BuiltInToolDefs.ApplyDiff), JsonUtils.SerializeCompact(payload));

        // Assert
        Assert.Contains("1234", result);
    }
}
