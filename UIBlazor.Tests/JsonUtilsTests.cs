namespace UIBlazor.Tests;

public class JsonUtilsTests
{
    private class TestObject
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }

    [Fact]
    public void Deserialize_CompleteJson_ReturnsAllFields()
    {
        // Arrange
        var _template = new
        {
            filePath = default(string),
            content = default(string)
        };
        var json = "{\"filePath\": \"config.json\", \"content\": \"hello\"}";

        // Act
        var result = JsonUtils.DeserializePartialAnonymousType(json, _template);

        // Assert
        Assert.Equal("config.json", result.filePath);
        Assert.Equal("hello", result.content);
    }

    [Fact]
    public void Deserialize_CutOnKey_IgnoresIncompleteKey()
    {
        // Arrange
        // Оборвалось на ключе "cont
        var _template = new
        {
            filePath = default(string),
            content = default(string)
        };
        var json = "{\"filePath\": \"config.json\", \"cont";

        // Act
        var result = JsonUtils.DeserializePartialAnonymousType(json, _template);

        // Assert
        Assert.Equal("config.json", result.filePath);
        Assert.Null(result.content); // Ключ проигнорирован
    }

    [Fact]
    public void Deserialize_CutOnTrailingComma_RemovesCommaAndCloses()
    {
        // Arrange
        // Оборвалось сразу после запятой
        var _template = new
        {
            filePath = default(string),
            content = default(string)
        };
        var json = "{\"filePath\": \"config.json\", ";

        // Act
        var result = JsonUtils.DeserializePartialAnonymousType(json, _template);

        // Assert
        Assert.Equal("config.json", result.filePath);
        Assert.Null(result.content);
    }

    [Fact]
    public void Deserialize_CutOnValue_ClosesQuotesAndReturnsPartialValue()
    {
        // Arrange
        // Оборвалось внутри значения "he
        var _template = new
        {
            filePath = default(string),
            content = default(string)
        };
        var json = "{\"filePath\": \"config.json\", \"content\": \"he";

        // Act
        var result = JsonUtils.DeserializePartialAnonymousType(json, _template);

        // Assert
        Assert.Equal("config.json", result.filePath);
        Assert.Equal("he", result.content); // Значение успешно прочитано частично
    }

    [Fact]
    public void Deserialize_CutOnColon_IgnoresKeyWithoutValue()
    {
        // Arrange
        // Оборвалось сразу на двоеточии после ключа
        var _template = new
        {
            filePath = default(string),
            content = default(string)
        };
        var json = "{\"filePath\": \"config.json\", \"content\"::";

        // Act
        var result = JsonUtils.DeserializePartialAnonymousType(json, _template);

        // Assert
        Assert.Equal("config.json", result.filePath);
        Assert.Null(result.content);
    }

    [Fact]
    public void Deserialize_CutOnEscapeCharacter_RemovesEscapeCharAndCloses()
    {
        // Arrange
        var _template = new
        {
            filePath = default(string),
            content = default(string)
        };
        var json = "{\"filePath\": \"C:\\\\Users\\\\Admin\\\\";

        // Act
        var result = JsonUtils.DeserializePartialAnonymousType(json, _template);

        // Assert
        Assert.Equal("C:\\Users\\Admin\\", result.filePath);
    }

    [Fact]
    public void Deserialize_CutInsideArrayValue_ClosesArrayCorrectly()
    {
        // Arrange
        var _complexTemplate = new
        {
            filePath = default(string),
            tags = default(string[]),
            meta = new
            {
                author = default(string),
                version = default(int)
            }
        };
        // Стрим оборвался внутри строкового значения внутри массива tags
        var json = "{\"filePath\": \"1.txt\", \"tags\": [\"git\", \"csh";

        // Act
        var result = JsonUtils.DeserializePartialAnonymousType(json, _complexTemplate);

        Assert.Equal("1.txt", result.filePath);
        Assert.NotNull(result.tags);
        Assert.Equal(2, result.tags.Length);
        Assert.Equal("git", result.tags[0]);
        Assert.Equal("csh", result.tags[1]); // Частичное значение закрылось кавычкой
    }

    [Fact]
    public void Deserialize_CutOnArrayComma_RemovesCommaAndClosesArray()
    {
        // Arrange
        var _complexTemplate = new
        {
            filePath = default(string),
            tags = default(string[]), // Массив строк
            meta = new
            {
                author = default(string),
                version = default(int)
            }
        };
        // Стрим оборвался сразу после запятой в массиве, перед следующим элементом
        var json = "{\"filePath\": \"1.txt\", \"tags\": [\"git\", ";

        // Act
        var result = JsonUtils.DeserializePartialAnonymousType(json, _complexTemplate);

        Assert.Equal("1.txt", result.filePath);
        Assert.NotNull(result.tags);
        Assert.Single(result.tags); // В массиве должен остаться ровно 1 валидный элемент
        Assert.Equal("git", result.tags[0]);
    }

    [Fact]
    public void Deserialize_CutInsideNestedObject_ClosesNestedObjectAndRoot()
    {
        // Arrange
        var _complexTemplate = new
        {
            filePath = default(string),
            tags = default(string[]), // Массив строк
            meta = new
            {
                author = default(string),
                version = default(int)
            }
        };
        // Стрим оборвался глубоко внутри вложенного объекта meta на значении author
        var json = "{\"filePath\": \"1.txt\", \"meta\": {\"author\": \"Jo";

        // Act
        var result = JsonUtils.DeserializePartialAnonymousType(json, _complexTemplate);

        Assert.Equal("1.txt", result.filePath);
        Assert.NotNull(result.meta);
        Assert.Equal("Jo", result.meta.author); // Вложенный объект успешно частично собран
        Assert.Equal(0, result.meta.version);   // Недошедшее поле осталось дефолтным
    }

    [Fact]
    public void Deserialize_CutOnNestedObjectKey_IgnoresIncompleteNestedKey()
    {
        // Arrange
        var _complexTemplate = new
        {
            filePath = default(string),
            tags = default(string[]), // Массив строк
            meta = new
            {
                author = default(string),
                version = default(int)
            }
        };
        // Стрим оборвался на ключе внутри вложенного объекта: "vers
        var json = "{\"filePath\": \"1.txt\", \"meta\": {\"author\": \"John\", \"vers";

        // Act
        var result = JsonUtils.DeserializePartialAnonymousType(json, _complexTemplate);

        Assert.Equal("1.txt", result.filePath);
        Assert.NotNull(result.meta);
        Assert.Equal("John", result.meta.author);
        Assert.Equal(0, result.meta.version); // Недописанный ключ проигнорирован, объект валиден
    }

    [Fact]
    public void Deserialize_CutOnNestedObjectColon_IgnoresKeyWithoutValueInNestedObject()
    {
        // Arrange
        var _complexTemplate = new
        {
            filePath = default(string),
            tags = default(string[]), // Массив строк
            meta = new
            {
                author = default(string),
                version = default(int)
            }
        };
        // Стрим оборвался на двоеточии после ключа во вложенном объекте
        var json = "{\"filePath\": \"1.txt\", \"meta\": {\"author\": \"John\", \"version\"::";

        // Act
        var result = JsonUtils.DeserializePartialAnonymousType(json, _complexTemplate);

        Assert.Equal("1.txt", result.filePath);
        Assert.NotNull(result.meta);
        Assert.Equal("John", result.meta.author);
        Assert.Equal(0, result.meta.version);
    }

    [Fact]
    public void Deserialize_EmptyOrInvalidStart_ReturnsDefaultTemplate()
    {
        // Arrange
        var _template = new
        {
            filePath = default(string),
            content = default(string)
        };

        // Act
        var resultEmpty = JsonUtils.DeserializePartialAnonymousType("", _template);
        var resultOnlyBrace = JsonUtils.DeserializePartialAnonymousType("{", _template);
        var resultFirstKeyCut = JsonUtils.DeserializePartialAnonymousType("{\"file", _template);

        // Assert
        Assert.Null(resultEmpty.filePath);
        Assert.Null(resultOnlyBrace.filePath);
        Assert.Null(resultFirstKeyCut.filePath);
    }

    [Fact]
    public void Serialize_ReturnsJsonString()
    {
        var obj = new TestObject { Name = "test", Value = 123 };
        var json = JsonUtils.Serialize(obj);
        Assert.Contains("\"name\": \"test\"", json);
        Assert.Contains("\"value\": 123", json);
    }

    [Fact]
    public void Deserialize_ReturnsObject()
    {
        var json = "{\"name\": \"test\", \"value\": 123}";
        var obj = JsonUtils.Deserialize<TestObject>(json);
        Assert.NotNull(obj);
        Assert.Equal("test", obj.Name);
        Assert.Equal(123, obj.Value);
    }

    [Fact]
    public void PrettyPrintFormat_ReturnsIndentedJson()
    {
        var minified = "{\"name\":\"test\",\"value\":123}";
        var pretty = JsonUtils.PrettyPrintFormat(minified);
        Assert.Contains("\n", pretty);
        Assert.Contains("  ", pretty); // Check for indentation
    }

    [Fact]
    public void DeserializeParameters_ReturnsDictionary()
    {
        var json = "{\"param1\": \"value1\", \"param2\": 2}";
        var dict = JsonUtils.DeserializeParameters(json);
        Assert.Equal("value1", dict["param1"].ToString());
        Assert.Equal("2", dict["param2"].ToString());
    }

    [Fact]
    public void DeserializeParameters_InvalidJson_ReturnsEmptyDictionary()
    {
        var json = "invalid json";
        var dict = JsonUtils.DeserializeParameters(json);
        Assert.Empty(dict);
    }

    [Fact]
    public void GetValue_ReturnsValueOrNull()
    {
        var dict = new Dictionary<string, object> { { "key", "value" } };
        Assert.Equal("value", dict.GetValue("key"));
        Assert.Null(dict.GetValue("missing"));
    }

    [Fact]
    public void GetString_ReturnsStringOrNull()
    {
        var dict = new Dictionary<string, object> { { "key", "value" }, { "intKey", 123 } };
        Assert.Equal("value", dict.GetString("key"));
        Assert.Equal("123", dict.GetString("intKey"));
        Assert.Null(dict.GetString("missing"));
    }

    [Fact]
    public void GetBool_ReturnsBoolOrDefault()
    {
        var dict = new Dictionary<string, object>
        {
            { "trueVal", "true" },
            { "falseVal", "false" },
            { "yesVal", "yes" },
            { "noVal", "no" },
            { "oneVal", "1" },
            { "zeroVal", "0" },
            { "invalid", "foo" }
        };

        Assert.True(dict.GetBool("trueVal"));
        Assert.False(dict.GetBool("falseVal"));
        Assert.True(dict.GetBool("yesVal"));
        Assert.False(dict.GetBool("noVal"));
        Assert.True(dict.GetBool("oneVal"));
        Assert.False(dict.GetBool("zeroVal"));
        Assert.False(dict.GetBool("invalid"));
        Assert.True(dict.GetBool("invalid", true));
    }

    [Fact]
    public void GetInt_ReturnsIntOrDefault()
    {
        var dict = new Dictionary<string, object> { { "key", 123 }, { "strKey", "456" }, { "invalid", "foo" } };
        Assert.Equal(123, dict.GetInt("key"));
        Assert.Equal(456, dict.GetInt("strKey"));
        Assert.Equal(0, dict.GetInt("invalid"));
        Assert.Equal(99, dict.GetInt("invalid", 99));
    }

    [Fact]
    public void GetObject_ReturnsDeserializedObject()
    {
        var innerObj = new TestObject { Name = "inner", Value = 999 };
        var dict = new Dictionary<string, object> { { "key", innerObj } };

        var result = dict.GetObject<TestObject>("key");
        Assert.NotNull(result);
        Assert.Equal("inner", result.Name);
        Assert.Equal(999, result.Value);
    }
}
