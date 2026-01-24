using UIBlazor.Utils;

namespace UIBlazor.Tests;

public class JsonUtilsTests
{
    private class TestObject
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
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
