namespace ToolCore.Tests;

public class JsonUtilsTests
{
    #region Serialize Tests

    [Fact]
    public void Serialize_Object_SerializesCorrectly()
    {
        var obj = new TestObject { Name = "test", Value = 42 };
        var json = JsonUtils.Serialize(obj);

        Assert.Contains("test", json);
        Assert.Contains("42", json);
    }

    [Fact]
    public void Serialize_Compact_OmitsWhitespace()
    {
        var obj = new TestObject { Name = "test", Value = 42 };
        var json = JsonUtils.SerializeCompact(obj);

        Assert.DoesNotContain("\n", json);
        Assert.DoesNotContain("  ", json);
    }

    [Fact]
    public void Serialize_NestedObject_SerializesCorrectly()
    {
        var obj = new NestedObject
        {
            Parent = new TestObject { Name = "parent", Value = 1 },
            Children = new List<TestObject>
            {
                new() { Name = "child1", Value = 2 },
                new() { Name = "child2", Value = 3 }
            }
        };

        var json = JsonUtils.Serialize(obj);

        Assert.Contains("parent", json);
        Assert.Contains("child1", json);
        Assert.Contains("child2", json);
    }
    #endregion

    #region Deserialize Tests

    [Fact]
    public void Deserialize_ValidJson_ReturnsObject()
    {
        var json = """{"name":"test","value":42}""";
        var result = JsonUtils.Deserialize<TestObject>(json);

        Assert.NotNull(result);
        Assert.Equal("test", result.Name);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Deserialize_InvalidJson_ReturnsNull()
    {
        var json = "invalid json";
        var result = JsonUtils.Deserialize<TestObject>(json);

        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_CamelCase_DeserializesCorrectly()
    {
        var json = """{"name":"test","value":42}""";
        var result = JsonUtils.Deserialize<TestObject>(json);

        Assert.NotNull(result);
        Assert.Equal("test", result.Name);
    }

    [Fact]
    public void Deserialize_EscapedCharacters_PreservesContent()
    {
        var json = """{"name":"Test \u003Cscript\u003E"}""";
        var result = JsonUtils.Deserialize<TestObject>(json);

        Assert.NotNull(result);
        Assert.Contains("Test", result.Name);
    }

    #endregion

    #region DeserializeParameters Tests

    [Theory]
    [InlineData("""{"key1":"value1","key2":123}""", 2)]
    [InlineData("""{"single":"value"}""", 1)]
    [InlineData("{}", 0)]
    public void DeserializeParameters_ValidJson_ReturnsExpectedCount(string json, int expectedCount)
    {
        var result = JsonUtils.DeserializeParameters(json);
        Assert.Equal(expectedCount, result.Count);
    }

    [Fact]
    public void DeserializeParameters_InvalidJson_ReturnsEmptyDictionary()
    {
        var json = "invalid json";
        var result = JsonUtils.DeserializeParameters(json);

        Assert.Empty(result);
    }

    #endregion

    #region GetValue Tests

    [Theory]
    [InlineData("key", "value")]
    [InlineData("existing", 42)]
    public void GetValue_ExistingKey_ReturnsValue(string key, object value)
    {
        var dict = new Dictionary<string, object> { { key, value } };
        var result = dict.GetValue(key);

        Assert.NotNull(result);
        Assert.Equal(value.ToString(), result?.ToString());
    }

    [Fact]
    public void GetValue_MissingKey_ReturnsNull()
    {
        var dict = new Dictionary<string, object>();
        var result = dict.GetValue("missing");

        Assert.Null(result);
    }

    #endregion

    #region GetString Tests

    [Theory]
    [InlineData("key", "value")]
    [InlineData("name", "test_string")]
    public void GetString_ValidString_ReturnsString(string key, string value)
    {
        var dict = new Dictionary<string, object> { { key, value } };
        var result = dict.GetString(key);

        Assert.Equal(value, result);
    }

    [Fact]
    public void GetString_MissingKey_ReturnsNull()
    {
        var dict = new Dictionary<string, object>();
        var result = dict.GetString("missing");

        Assert.Null(result);
    }

    #endregion

    #region GetBool Tests

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void GetBool_VariousValues_ReturnsExpectedResult(object value, bool expected)
    {
        var dict = new Dictionary<string, object> { { "key", value } };
        var result = dict.GetBool("key");

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetBool_MissingKey_ReturnsDefault(bool defaultValue)
    {
        var dict = new Dictionary<string, object>();
        var result = dict.GetBool("missing", defaultValue: defaultValue);

        Assert.Equal(defaultValue, result);
    }

    #endregion

    #region GetInt Tests

    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(999999)]
    public void GetInt_ValidInt_ReturnsInt(int value)
    {
        var dict = new Dictionary<string, object> { { "key", value } };
        var result = dict.GetInt("key");

        Assert.Equal(value, result);
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData("0", 0)]
    [InlineData("-100", -100)]
    public void GetInt_StringNumber_ReturnsInt(string value, int expected)
    {
        var dict = new Dictionary<string, object> { { "key", value } };
        var result = dict.GetInt("key");

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(-50)]
    [InlineData(0)]
    public void GetInt_MissingKey_ReturnsDefault(int defaultValue)
    {
        var dict = new Dictionary<string, object>();
        var result = dict.GetInt("missing", defaultValue: defaultValue);

        Assert.Equal(defaultValue, result);
    }

    #endregion

    #region GetDictionary Tests

    [Fact]
    public void GetDictionary_ValidDict_ReturnsDictionary()
    {
        var innerDict = new Dictionary<string, string> { { "inner", "value" } };
        var dict = new Dictionary<string, object> { { "key", innerDict } };

        var result = dict.GetDictionary("key");

        Assert.NotNull(result);
        Assert.Equal("value", result["inner"]);
    }

    #endregion

    #region GetObject Tests

    [Fact]
    public void GetObject_ValidObject_ReturnsObject()
    {
        var json = """{"name":"test","value":42}""";
        var dict = JsonUtils.DeserializeParameters(json);

        var result = dict.GetObject<TestObject>("key");

        Assert.Null(result);
    }

    [Fact]
    public void GetObject_JsonElement_ReturnsObject()
    {
        var json = """{"name":"test","value":42}""";
        using var doc = JsonDocument.Parse(json);

        var result = doc.RootElement.GetObject<TestObject>();

        Assert.NotNull(result);
        Assert.Equal("test", result.Name);
        Assert.Equal(42, result.Value);
    }

    #endregion

    private class TestObject
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }

    private class NestedObject
    {
        public TestObject Parent { get; set; } = new();
        public List<TestObject> Children { get; set; } = new();
    }
}
