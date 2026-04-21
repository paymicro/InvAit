using System.Text;
using System.Text.Json;
using UIBlazor.Processors;
using UIBlazor.Processors.Models;

namespace UIBlazor.Tests.Processors;

public partial class SchemaProcessorTests
{
    #region Basic Output Tests

    [Fact]
    public void AppendSchemaDescription_NullProperties_ProducesEmptyOutput()
    {
        var prop = new JsonSchemaProperty { Type = "string" };
        var sb = new StringBuilder();
        SchemaProcessor.AppendSchemaDescription(sb, prop, "root");
        Assert.Equal(string.Empty, sb.ToString().Trim());
    }

    [Fact]
    public void AppendSchemaDescription_SimpleProperty_FormatsCorrectly()
    {
        var prop = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["name"] = new JsonSchemaProperty { Type = "string", Description = "User name" }
            }
        };
        var sb = new StringBuilder();
        SchemaProcessor.AppendSchemaDescription(sb, prop, "");
        var output = sb.ToString();
        Assert.Contains("name : [string]", output);
        Assert.Contains("User name", output);
    }

    [Fact]
    public void AppendSchemaDescription_EmptyProperties_DoesNotThrow()
    {
        var prop = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>()
        };
        var sb = new StringBuilder();
        var exception = Record.Exception(() => SchemaProcessor.AppendSchemaDescription(sb, prop, ""));
        Assert.Null(exception);
        Assert.Empty(sb.ToString());
    }

    #endregion

    #region Enum Tests

    [Fact]
    public void AppendSchemaDescription_PropertyWithEnum_IncludesEnumInfo()
    {
        var prop = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["color"] = new JsonSchemaProperty
                {
                    Type = "string",
                    EnumValues = new List<object> { JsonDocument.Parse("\"red\"").RootElement, JsonDocument.Parse("\"blue\"").RootElement }
                }
            }
        };
        var sb = new StringBuilder();
        SchemaProcessor.AppendSchemaDescription(sb, prop, "");
        var output = sb.ToString();
        Assert.Contains("enum:", output);
        Assert.Contains("red", output);
        Assert.Contains("blue", output);
    }

    [Fact]
    public void AppendSchemaDescription_ArrayWithEnumItems_ShowsEnumInItemType()
    {
        var prop = new JsonSchemaProperty
        {
            Type = "array",
            Items = new JsonSchemaProperty
            {
                Type = "string",
                EnumValues = new List<object> { JsonDocument.Parse("\"active\"").RootElement, JsonDocument.Parse("\"inactive\"").RootElement }
            },
            Properties = null
        };
        var sb = new StringBuilder();
        SchemaProcessor.AppendSchemaDescription(sb, prop, "status");
        var output = sb.ToString();
        Assert.Contains("enum:", output);
        Assert.Contains("active", output);
    }

    #endregion

    #region Constraint Tests

    [Theory]
    [InlineData("Minimum", "min", 10.0)]
    [InlineData("Maximum", "max", 100.0)]
    [InlineData("MinLength", "minLen", 5)]
    [InlineData("MaxLength", "maxLen", 50)]
    public void AppendSchemaDescription_Constraints_IncludesConstraintInfo(string constraintType, string expectedOutput, object value)
    {
        var prop = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["field"] = constraintType switch
                {
                    "Minimum" => new JsonSchemaProperty { Type = "number", Minimum = (double)value },
                    "Maximum" => new JsonSchemaProperty { Type = "number", Maximum = (double)value },
                    "MinLength" => new JsonSchemaProperty { Type = "string", MinLength = (int)value },
                    "MaxLength" => new JsonSchemaProperty { Type = "string", MaxLength = (int)value },
                    _ => new JsonSchemaProperty { Type = "string" }
                }
            }
        };
        var sb = new StringBuilder();
        SchemaProcessor.AppendSchemaDescription(sb, prop, "");
        var output = sb.ToString();
        Assert.Contains($"({expectedOutput}:", output);
    }

    [Fact]
    public void AppendSchemaDescription_PropertyWithPattern_IncludesPatternInfo()
    {
        var prop = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["email"] = new JsonSchemaProperty { Type = "string", Pattern = "^[^@]+@[^@]+$" }
            }
        };
        var sb = new StringBuilder();
        SchemaProcessor.AppendSchemaDescription(sb, prop, "");
        var output = sb.ToString();
        Assert.Contains("pattern:", output);
    }

    [Fact]
    public void AppendSchemaDescription_MultipleConstraints_AllIncluded()
    {
        var prop = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["field"] = new JsonSchemaProperty
                {
                    Type = "string",
                    MinLength = 1,
                    MaxLength = 100,
                    Pattern = "^[a-z]+$"
                }
            }
        };
        var sb = new StringBuilder();
        SchemaProcessor.AppendSchemaDescription(sb, prop, "");
        var output = sb.ToString();
        Assert.Contains("(minLen:", output);
        Assert.Contains("(maxLen:", output);
        Assert.Contains("(pattern:", output);
    }

    #endregion

    #region Nested Object Tests

    [Fact]
    public void AppendSchemaDescription_NestedObject_RecursesCorrectly()
    {
        var prop = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["user"] = new JsonSchemaProperty
                {
                    Type = "object",
                    Properties = new Dictionary<string, JsonSchemaProperty>
                    {
                        ["name"] = new JsonSchemaProperty { Type = "string" }
                    }
                }
            }
        };
        var sb = new StringBuilder();
        SchemaProcessor.AppendSchemaDescription(sb, prop, "");
        var output = sb.ToString();
        Assert.Contains("user.name", output);
    }

    [Fact]
    public void AppendSchemaDescription_NestedPath_FormatsCorrectly()
    {
        var prop = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["parent"] = new JsonSchemaProperty
                {
                    Type = "object",
                    Properties = new Dictionary<string, JsonSchemaProperty>
                    {
                        ["child"] = new JsonSchemaProperty { Type = "string" }
                    }
                }
            }
        };
        var sb = new StringBuilder();
        SchemaProcessor.AppendSchemaDescription(sb, prop, "root");
        var output = sb.ToString();
        Assert.Contains("root.parent.child", output);
    }

    #endregion

    #region Array Tests

    [Fact]
    public void AppendSchemaDescription_ArrayProperty_FormatsCorrectly()
    {
        var prop = new JsonSchemaProperty
        {
            Type = "array",
            Items = new JsonSchemaProperty { Type = "string" },
            Properties = null
        };
        var sb = new StringBuilder();
        SchemaProcessor.AppendSchemaDescription(sb, prop, "tags");
        var output = sb.ToString();
        Assert.Contains("tags[]", output);
        Assert.Contains("array of string", output);
    }

    [Fact]
    public void AppendSchemaDescription_ArrayOfObjects_RecursesIntoItems()
    {
        var prop = new JsonSchemaProperty
        {
            Type = "array",
            Items = new JsonSchemaProperty
            {
                Type = "object",
                Properties = new Dictionary<string, JsonSchemaProperty>
                {
                    ["name"] = new JsonSchemaProperty { Type = "string" }
                }
            },
            Properties = null
        };
        var sb = new StringBuilder();
        SchemaProcessor.AppendSchemaDescription(sb, prop, "users");
        var output = sb.ToString();
        Assert.Contains("users[]_item.name", output);
    }

    #endregion

    #region Type Tests

    [Fact]
    public void AppendSchemaDescription_UnknownType_ShowsUnknown()
    {
        var prop = new JsonSchemaProperty
        {
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["unknown"] = new JsonSchemaProperty { Type = null }
            }
        };
        var sb = new StringBuilder();
        SchemaProcessor.AppendSchemaDescription(sb, prop, "");
        var output = sb.ToString();
        Assert.Contains("unknown", output);
    }

    #endregion
}
