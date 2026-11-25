namespace UIBlazor.Tests.Agents;

#pragma warning disable xUnit1013 // Public method should be marked as test
#pragma warning disable CA1822 // Mark members as static
public partial class BuiltInToolDefsTests
{
    // Метод без параметров
    public void MethodWithoutParameters() { }

    // Метод с описанием
    [DisplayName("display_name")]
    [Description("Test method description")]
    public string MethodWithDescription(string param1)
    {
        return param1;
    }

    // Метод без описания
    public void MethodWithoutDescription(int param1) { }

    // Метод с параметром имеющим описание
    public void MethodWithParameterDescription(
        [Description("Parameter description")] string param1)
    { }

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
        Guid guidParam)
    { }

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
        [Description("Third parameter")] bool param3 = false)
    { }

    // Метод с nullable типом
    public void MethodWithNullableType(int? nullableInt, string? nullableString) { }

    // Метод с enum
    public void MethodWithEnum(TestEnum enumParam) { }

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

    [Fact]
    public void MapMethodToTool_MethodWithoutParameters_ReturnsEmptyProperties()
    {
        // Arrange
        var method = GetType().GetMethod(nameof(MethodWithoutParameters))!;

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert
        Assert.Equal("undefined", result.Function.Name);
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
        Assert.Equal("display_name", result.Function.Name);
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
    public void MapMethodToTool_CreateFile_ModeIsNullableUnionTypeInRequired()
    {
        // `mode` decides overwrite / append / prepend. It must stay OPTIONAL: an existing prompt that
        // omits it keeps the old overwrite behaviour, and strict mode would otherwise force the model
        // to send a value it does not care about.
        var method = typeof(BuiltInToolDefs).GetMethod(nameof(BuiltInToolDefs.CreateFile));

        var result = BuiltInToolDefs.MapMethodToTool(method);

        Assert.Equal(BuiltInToolEnum.CreateFile, result.Function.Name);
        Assert.Contains("overwrite", result.Function.Parameters.Properties["mode"].Description, StringComparison.Ordinal);
        Assert.Contains("append", result.Function.Parameters.Properties["mode"].Description, StringComparison.Ordinal);
        Assert.Contains("prepend", result.Function.Parameters.Properties["mode"].Description, StringComparison.Ordinal);
        Assert.Contains("editorconfig", result.Function.Description, StringComparison.OrdinalIgnoreCase);

        // Strict mode keeps every parameter in `required`; optionality is expressed as a NULLABLE
        // type, so the model may pass `"mode": null` and still get the overwrite default.
        Assert.Equal(["filePath", "content", "mode"], result.Function.Parameters.Required);
        var mode = result.Function.Parameters.Properties["mode"];
        Assert.True(mode.IsUnionType);
        Assert.Equal([NativeToolType.String, NativeToolType.Null], (string[])mode.Type);
    }

    [Fact]
    public void MapMethodToTool_EditFile_ReturnsCorrectPropertyDescription()
    {
        // Arrange
        var method = typeof(BuiltInToolDefs).GetMethod(nameof(BuiltInToolDefs.EditFile));

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert — the contract the model depends on, field by field. A whole-JSON snapshot of a
        // multi-line prose description was pure maintenance tax: it broke on every wording change
        // and never caught a real schema defect.
        Assert.Equal(BuiltInToolEnum.EditFile, result.Function.Name);
        Assert.True(result.Function.Parameters.Properties.ContainsKey("filePath"));
        // Не `Equal("File path")` в чистом виде: edit_file не умеет создавать файл, и описание
        // должно это говорить. Проверяем СМЫСЛ (путь + существующий + отсылка к create_file),
        // а не точную фразу — иначе тест снова начнёт защищать неудачную формулировку.
        Assert.Contains("file", result.Function.Parameters.Properties["filePath"].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXISTING", result.Function.Parameters.Properties["filePath"].Description, StringComparison.Ordinal);
        Assert.Contains("create_file", result.Function.Parameters.Properties["filePath"].Description, StringComparison.Ordinal);
        Assert.Contains("exactly ONE place", result.Function.Description, StringComparison.Ordinal);

        var edits = result.Function.Parameters.Properties["edits"];
        Assert.Equal("array", edits.Type);

        var items = Assert.IsType<NativeParameters>(edits.Items);

        // Strict mode: ВСЕ поля вложенного объекта в `required` — так же, как на верхнем
        // уровне, где опциональность выражается ТОЛЬКО nullable-типом. Раньше union-поля
        // (approximateLine, replaceAll) выкидывались из required, и валидирующий endpoint
        // отклонял весь запрос, а не один инструмент. Состав сравнивается без учёта
        // порядка: контракт — это набор полей, а не порядок рефлексии.
        Assert.Equal(
            ["approximateLine", "newStr", "oldStr", "replaceAll"],
            items.Required.OrderBy(x => x, StringComparer.Ordinal).ToList());

        // replaceAll ОПЦИОНАЛЕН семантически: union с null позволяет модели прислать null
        // (и VS-хост обязан трактовать это как false), но поле остаётся в required.
        var replaceAll = items.Properties!["replaceAll"];
        Assert.True(replaceAll.IsUnionType);
        Assert.Equal([NativeToolType.Boolean, NativeToolType.Null], (string[])replaceAll.Type);
        Assert.Contains("Replace every occurrence", replaceAll.Description, StringComparison.Ordinal);

        // approximateLine — та же форма: integer|null, null легален.
        var approximateLine = items.Properties!["approximateLine"];
        Assert.True(approximateLine.IsUnionType);
        Assert.Equal([NativeToolType.Integer, NativeToolType.Null], (string[])approximateLine.Type);
    }

    [Fact]
    public void MapMethodToTool_EditFile_NoFabricatedDefaultNoteOnNestedFields()
    {
        // [DefaultValue] on DiffEdit.ReplaceAll / ApproximateLine added NOTHING to the schema:
        // IsNullableProperty already reports `bool?`/`int?` as optional, so the union with null is
        // produced by the TYPE. The attributes only appended " (default 'False')" /
        // " (default '-1')" to descriptions that already state the default in prose — a second
        // source of truth for one default, and one of them factually wrong. They are removed; the
        // descriptions below are the ones the model actually reads.
        var method = typeof(BuiltInToolDefs).GetMethod(nameof(BuiltInToolDefs.EditFile));

        var result = BuiltInToolDefs.MapMethodToTool(method);

        var items = Assert.IsType<NativeParameters>(result.Function.Parameters.Properties["edits"].Items);

        var replaceAll = items.Properties!["replaceAll"];
        Assert.DoesNotContain("(default", replaceAll.Description, StringComparison.Ordinal);
        Assert.Contains("Default: false", replaceAll.Description, StringComparison.Ordinal);

        var approximateLine = items.Properties!["approximateLine"];
        Assert.DoesNotContain("(default", approximateLine.Description, StringComparison.Ordinal);
        // Здесь раньше стоял Assert.Equal("Approximate start line or null", …) — он ЗАКРЕПЛЯЛ текст,
        // в котором не было ни 1-based, ни окна ±5, ни того, что устаревшая подсказка молча
        // отбрасывается. Проверяем те три факта, которых модели не хватало.
        Assert.Contains("1-based", approximateLine.Description, StringComparison.Ordinal);
        Assert.Contains("+/-5", approximateLine.Description, StringComparison.Ordinal);
        Assert.Contains("IGNORED", approximateLine.Description, StringComparison.Ordinal);

        // Правило «пустой oldStr — это НЕ точка вставки, а ближайшая пустая строка» переехало на
        // поле, к которому относится, вместо простыни в описании инструмента. Тест держит его на
        // новом месте, иначе факт, которого модели не хватало, тихо исчезнет при следующей
        // чистке текста.
        var oldStr = items.Properties!["oldStr"];
        Assert.Contains("NOT an insertion point", oldStr.Description, StringComparison.Ordinal);
        Assert.Contains("BLANK line", oldStr.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void MapMethodToTool_EditFile_DryRunIsNullableBooleanInRequired()
    {
        // dryRun был реализован в VS-хосте и покрыт тестами в VS Code, но НЕ объявлен в схеме —
        // schema-validated вызов не мог его прислать, то есть ветка была мёртвой. Теперь он
        // есть и опционален той же формой, что остальные optional-поля: bool? в required.
        var method = typeof(BuiltInToolDefs).GetMethod(nameof(BuiltInToolDefs.EditFile));

        var result = BuiltInToolDefs.MapMethodToTool(method);

        Assert.Equal(["filePath", "edits", "dryRun"], result.Function.Parameters.Required);

        var dryRun = result.Function.Parameters.Properties["dryRun"];
        Assert.True(dryRun.IsUnionType);
        Assert.Equal([NativeToolType.Boolean, NativeToolType.Null], (string[])dryRun.Type);
        Assert.Contains("WOULD change", dryRun.Description, StringComparison.Ordinal);
        Assert.Contains("without writing", dryRun.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void MapMethodToTool_Bash_HasDescriptionParameterRequired()
    {
        // Arrange
        var method = typeof(BuiltInToolDefs).GetMethod(nameof(BuiltInToolDefs.Bash));

        // Act
        var result = BuiltInToolDefs.MapMethodToTool(method);

        // Assert - strict-схема делает обязательность сама, если параметр объявлен без DefaultValue:
        // модель обязана назвать намерение команды, иначе плоское поле осталось бы опциональным
        Assert.Equal(
            ["command", "description"],
            result.Function.Parameters.Required.OrderBy(x => x == "command" ? 0 : 1));
        var description = result.Function.Parameters.Properties["description"];
        Assert.False(description.IsUnionType);
        Assert.Equal("string", description.Type);
        Assert.Contains("what the command does", description.Description, StringComparison.OrdinalIgnoreCase);
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
}
