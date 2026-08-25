namespace UIBlazor.Tests.Components.ToolViews;

/// <summary>
/// Tests for <see cref="JsonArgsView"/>
/// </summary>
public class JsonArgsViewTests : BunitContext
{
    public JsonArgsViewTests()
    {
        // The component injects IToolManager even though it is not used in markup
        Services.AddSingleton(new Mock<IToolManager>().Object);
        Services.AddRadzenComponents();
    }

    #region Parsing Tests

    [Fact]
    public void ShouldRenderNothing_WhenArgsEmpty()
    {
        // Act
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, string.Empty));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".json-args-view"));
    }

    [Fact]
    public void ShouldRenderNothing_WhenArgsWhitespace()
    {
        // Act
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, "   "));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".json-args-view"));
    }

    [Fact]
    public void ShouldRenderNothing_WhenArgsInvalidJson()
    {
        // Arrange - RepairJson cannot fix plain garbage
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, "not json at all"));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".json-args-view"));
    }

    [Fact]
    public void ShouldRenderNothing_WhenArgsEmptyObject()
    {
        // Act
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, "{}"));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".json-args-view"));
    }

    #endregion

    #region Scalar Value Rendering Tests

    [Fact]
    public void ShouldRenderStringKeyValue()
    {
        // Act
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, """{"path":"src/file.cs"}"""));

        // Assert
        var row = cut.Find(".json-args-row");
        Assert.Contains("path", row.QuerySelector(".json-args-key")!.TextContent);
        Assert.Equal("src/file.cs", row.QuerySelector(".json-args-value")!.TextContent);
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    public void ShouldRenderBoolValue(string json, string expected)
    {
        // Act
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, $$"""{"enabled":{{json}}}"""));

        // Assert
        Assert.Equal(expected, cut.Find(".json-args-bool").TextContent);
    }

    [Fact]
    public void ShouldRenderNumberValue()
    {
        // Act
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, """{"limit":42}"""));

        // Assert
        Assert.Equal("42", cut.Find(".json-args-number").TextContent);
    }

    [Fact]
    public void ShouldRenderNullValue()
    {
        // Act
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, """{"filter":null}"""));

        // Assert
        Assert.Equal("null", cut.Find(".json-args-null").TextContent);
    }

    [Fact]
    public void ShouldRenderLongString_InLongValueBlock()
    {
        // Arrange - string longer than 200 chars must use the long-value element
        var longValue = new string('x', 250);

        // Act
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, $$"""{"content":"{{longValue}}"}"""));

        // Assert
        var longDiv = cut.Find(".json-args-value--long");
        Assert.NotNull(longDiv);
        Assert.Equal(longValue, longDiv.TextContent);
    }

    #endregion

    #region Nested Structure Rendering Tests

    [Fact]
    public void ShouldRenderNestedObject()
    {
        // Act
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, """
                              {
                                "outer": { "inner": "value" }
                              }
                              """));

        // Assert
        var nested = cut.FindAll(".json-args-nested");
        Assert.True(nested.Count >= 2); // outer container + nested container
        Assert.Contains("inner", cut.Markup);
        Assert.Contains("value", cut.Markup);
    }

    [Fact]
    public void ShouldRenderArrayItems()
    {
        // Act
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, """{"files":["a.cs","b.cs"]}"""));

        // Assert
        var items = cut.FindAll(".json-args-array-item");
        Assert.Equal(2, items.Count);
        Assert.NotEmpty(cut.FindAll(".json-args-array-marker"));
        Assert.Contains("a.cs", items[0].TextContent);
        Assert.Contains("b.cs", items[1].TextContent);
    }

    [Fact]
    public void ShouldRenderMultipleKeys_InOrder()
    {
        // Act
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, """{"first":"1","second":"2","third":"3"}"""));

        // Assert
        var keys = cut.FindAll(".json-args-key").Select(k => k.TextContent).ToList();
        Assert.Equal(["first", "second", "third"], keys);
    }

    #endregion

    #region Partial JSON Tests

    [Fact]
    public void ShouldRepairTruncatedValue_AndRenderAvailablePairs()
    {
        // Act - streaming-style truncated JSON value is closed by JsonUtils
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, """{"path":"src/ap"""));

        // Assert
        Assert.Contains("path", cut.Markup);
        var value = cut.Find(".json-args-value");
        Assert.Contains("src/ap", value.TextContent);
    }

    [Fact]
    public void ShouldDropOrphanKey_WithoutValue()
    {
        // Act - trailing incomplete key "opt" must be dropped during repair
        var cut = Render<JsonArgsView>(parameters => parameters
            .Add(p => p.Args, """{"path":"src/app.cs","opt"""));

        // Assert - only complete pairs survive
        var keys = cut.FindAll(".json-args-key").Select(k => k.TextContent).ToList();
        Assert.Equal(["path"], keys);
        Assert.Contains("src/app.cs", cut.Markup);
    }

    #endregion
}
