namespace UIBlazor.Tests.Components.Settings;

using UIBlazor.Processors.Models;

/// <summary>
/// Tests for <see cref="MCPShemaProperties"/>
/// </summary>
public class McpShemaPropertiesTests : BunitContext
{
    public McpShemaPropertiesTests()
    {
        Services.AddRadzenComponents();

        // Radzen inputs may touch JS interop during lifecycle - silence via Moq
        var mockJsRuntime = new Mock<IJSRuntime>();
        mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>(It.IsAny<string>(), It.IsAny<object[]>()))
            .ReturnsAsync((IJSObjectReference?)null!);
        mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
            .ReturnsAsync((IJSObjectReference?)null!);
        Services.AddSingleton(mockJsRuntime.Object);

        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
    }

    private static JsonSchemaProperty CreateProperty(string key, string type, string? description = null)
    {
        return new JsonSchemaProperty
        {
            Type = "object",
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                [key] = new() { Type = type, Description = description }
            }
        };
    }

    #region Rendering Tests

    [Fact]
    public void NullSchema_RendersNothing()
    {
        // Act
        var cut = Render<MCPShemaProperties>(parameters => parameters
            .Add(p => p.SchemaProperty, (JsonSchemaProperty?)null));

        // Assert
        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public void StringProperty_RendersTextBox_WithPlaceholderAndDescription()
    {
        // Arrange
        var schema = CreateProperty("name", "string", "The project name");
        var values = new Dictionary<string, object?>();

        // Act
        var cut = Render<MCPShemaProperties>(parameters => parameters
            .Add(p => p.SchemaProperty, schema)
            .Add(p => p.Values, values));

        // Assert
        var textBox = cut.FindComponent<RadzenTextBox>();
        Assert.Equal("string", textBox.Instance.Placeholder);

        // Description is shown as the field text, key as a label
        Assert.Contains("The project name", cut.Markup);
        Assert.Contains("name", cut.FindComponents<RadzenLabel>().Select(l => l.Instance.Text).ToList());
    }

    [Fact]
    public void NumberProperty_RendersDoubleNumeric()
    {
        // Arrange
        var schema = CreateProperty("ratio", "number");

        // Act
        var cut = Render<MCPShemaProperties>(parameters => parameters
            .Add(p => p.SchemaProperty, schema));

        // Assert
        var numeric = cut.FindComponent<RadzenNumeric<double?>>();
        Assert.Equal("number", numeric.Instance.Placeholder);
        Assert.Empty(cut.FindComponents<RadzenNumeric<int?>>());
    }

    [Fact]
    public void IntegerProperty_RendersIntNumeric()
    {
        // Arrange
        var schema = CreateProperty("count", "integer");

        // Act
        var cut = Render<MCPShemaProperties>(parameters => parameters
            .Add(p => p.SchemaProperty, schema));

        // Assert
        var numeric = cut.FindComponent<RadzenNumeric<int?>>();
        Assert.Equal("integer", numeric.Instance.Placeholder);
        Assert.Empty(cut.FindComponents<RadzenNumeric<double?>>());
    }

    [Fact]
    public void BooleanProperty_RendersSwitch()
    {
        // Arrange
        var schema = CreateProperty("enabled", "boolean");

        // Act
        var cut = Render<MCPShemaProperties>(parameters => parameters
            .Add(p => p.SchemaProperty, schema));

        // Assert
        Assert.Single(cut.FindComponents<RadzenSwitch>());
    }

    [Theory]
    [InlineData("array of string", "string")]
    [InlineData("array of number", "number")]
    public void ArrayProperty_ShowsItemsTypeInPlaceholder(string expected, string? itemsType)
    {
        // Arrange
        var schema = CreateProperty("tags", "array");
        schema.Properties!["tags"].Items = new JsonSchemaProperty { Type = itemsType };

        // Act
        var cut = Render<MCPShemaProperties>(parameters => parameters
            .Add(p => p.SchemaProperty, schema));

        // Assert
        Assert.Contains(expected, cut.Markup);
    }

    [Fact]
    public void ArrayProperty_WithoutItems_ShowsPlaceholderWithoutType()
    {
        // Arrange
        var schema = CreateProperty("tags", "array");
        schema.Properties!["tags"].Items = null;

        // Act
        var cut = Render<MCPShemaProperties>(parameters => parameters
            .Add(p => p.SchemaProperty, schema));

        // Assert
        Assert.Contains("array of", cut.Markup);
    }

    [Fact]
    public void ObjectProperty_RendersNestedRecursion()
    {
        // Arrange
        var nested = new JsonSchemaProperty
        {
            Type = "object",
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["before"] = new() { Type = "string" },
                ["after"] = new() { Type = "string" }
            }
        };
        var schema = CreateProperty("context", "object");
        schema.Properties!["context"].Properties = nested.Properties;

        // Act
        var cut = Render<MCPShemaProperties>(parameters => parameters
            .Add(p => p.SchemaProperty, schema));

        // Assert - one nested MCPShemaProperties instance (root is the cut itself);
        // two text boxes come from the nested "before"/"after" string fields
        Assert.Single(cut.FindComponents<MCPShemaProperties>());
        Assert.Equal(2, cut.FindComponents<RadzenTextBox>().Count);
        Assert.Contains("before", cut.Markup);
        Assert.Contains("after", cut.Markup);
    }

    [Fact]
    public void RequiredKey_MarksFieldWithRequiredClass()
    {
        // Arrange
        var schema = new JsonSchemaProperty
        {
            Type = "object",
            Properties = new Dictionary<string, JsonSchemaProperty>
            {
                ["must"] = new() { Type = "string" },
                ["optional"] = new() { Type = "string" }
            },
            Required = ["must"]
        };

        // Act
        var cut = Render<MCPShemaProperties>(parameters => parameters
            .Add(p => p.SchemaProperty, schema));

        // Assert - exactly one field carries the required marker class
        var requiredFields = cut.FindAll(".required");
        Assert.Single(requiredFields);
        Assert.Contains("must", requiredFields[0].TextContent);
    }

    #endregion

    #region Values Binding Tests

    [Fact]
    public void Render_InitializesValuesKeys_WithNull()
    {
        // Arrange
        var schema = CreateProperty("name", "string");
        var values = new Dictionary<string, object?>();

        // Act
        Render<MCPShemaProperties>(parameters => parameters
            .Add(p => p.SchemaProperty, schema)
            .Add(p => p.Values, values));

        // Assert
        Assert.True(values.ContainsKey("name"));
        Assert.Null(values["name"]);
    }

    [Fact]
    public async Task TextValueChanged_WritesToValuesDictionary()
    {
        // Arrange
        var schema = CreateProperty("name", "string");
        var values = new Dictionary<string, object?>();

        var cut = Render<MCPShemaProperties>(parameters => parameters
            .Add(p => p.SchemaProperty, schema)
            .Add(p => p.Values, values));

        // Act
        var textBox = cut.FindComponent<RadzenTextBox>();
        await cut.InvokeAsync(() => textBox.Instance.ValueChanged.InvokeAsync("typed value"));

        // Assert
        Assert.Equal("typed value", values["name"]);
    }

    [Fact]
    public async Task IntegerValueChanged_WritesToValuesDictionary()
    {
        // Arrange
        var schema = CreateProperty("count", "integer");
        var values = new Dictionary<string, object?>();

        var cut = Render<MCPShemaProperties>(parameters => parameters
            .Add(p => p.SchemaProperty, schema)
            .Add(p => p.Values, values));

        // Act
        var numeric = cut.FindComponent<RadzenNumeric<int?>>();
        await cut.InvokeAsync(() => numeric.Instance.ValueChanged.InvokeAsync(7));

        // Assert
        Assert.Equal(7, values["count"]);
    }

    #endregion
}
