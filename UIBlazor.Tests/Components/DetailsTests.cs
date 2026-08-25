namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for <see cref="Details"/>
/// </summary>
public class DetailsTests : BunitContext
{
    public DetailsTests()
    {
        Services.AddRadzenComponents();
    }

    #region Rendering Tests

    [Fact]
    public void ShouldRenderContainer()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.Text, "Header")
            .AddChildContent("<div class=\"inner\">content</div>"));

        // Assert
        Assert.NotNull(cut.Find(".custom-details"));
        Assert.NotNull(cut.Find(".header"));
        Assert.NotNull(cut.Find(".content-wrapper"));
        Assert.NotNull(cut.Find(".content-inner"));
    }

    [Fact]
    public void ShouldRenderHeaderText()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.Text, "My Section")
            .AddChildContent("content"));

        // Assert
        var headerLeft = cut.Find(".header-left");
        Assert.Contains("My Section", headerLeft.TextContent);
    }

    [Fact]
    public void ShouldRenderArrow()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .AddChildContent("content"));

        // Assert
        var arrow = cut.Find(".arrow");
        Assert.Equal("▼", arrow.TextContent);
    }

    [Fact]
    public void ShouldRenderChildContent()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .AddChildContent("<div class=\"child-marker\">child content</div>"));

        // Assert
        var child = cut.Find(".child-marker");
        Assert.Equal("child content", child.TextContent);
    }

    #endregion

    #region Icon Tests

    [Fact]
    public void ShouldRenderIcon_WhenIconProvided()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.Icon, "fa-solid fa-lightbulb")
            .AddChildContent("content"));

        // Assert - class attribute is split into individual CSS classes
        var icon = cut.Find("i.icon-spacing");
        Assert.Contains("fa-solid", icon.ClassList);
        Assert.Contains("fa-lightbulb", icon.ClassList);
    }

    [Fact]
    public void ShouldNotRenderIcon_WhenIconNotProvided()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .AddChildContent("content"));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find("i.icon-spacing"));
    }

    #endregion

    #region CSS State Tests

    [Fact]
    public void ShouldNotHaveExpandedClass_ByDefault()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .AddChildContent("content"));

        // Assert
        var container = cut.Find(".custom-details");
        Assert.DoesNotContain("is-expanded", container.ClassList);
    }

    [Fact]
    public void ShouldHaveExpandedClass_WhenIsExpandedIsTrue()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.IsExpanded, true)
            .AddChildContent("content"));

        // Assert
        var container = cut.Find(".custom-details");
        Assert.Contains("is-expanded", container.ClassList);
    }

    [Fact]
    public void ShouldHaveRoundedClass_WhenIsRoundedIsTrue()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.IsRounded, true)
            .AddChildContent("content"));

        // Assert
        var container = cut.Find(".custom-details");
        Assert.Contains("is-rounded", container.ClassList);
    }

    [Fact]
    public void ShouldAppendCustomClass_FromAdditionalAttributes()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.AdditionalAttributes, new Dictionary<string, object> { ["class"] = "reasoning-details" })
            .AddChildContent("content"));

        // Assert
        var container = cut.Find(".custom-details.reasoning-details");
        Assert.NotNull(container);
    }

    #endregion

    #region Interaction Tests

    [Fact]
    public async Task ClickHeader_TogglesExpandedState()
    {
        // Arrange - collapsed by default
        var cut = Render<Details>(parameters => parameters
            .AddChildContent("content"));

        var header = cut.Find(".header");

        // Act
        await cut.InvokeAsync(() => header.Click());

        // Assert
        Assert.Contains("is-expanded", cut.Find(".custom-details").ClassList);

        // Act - toggle back
        await cut.InvokeAsync(() => cut.Find(".header").Click());

        // Assert
        Assert.DoesNotContain("is-expanded", cut.Find(".custom-details").ClassList);
    }

    [Fact]
    public async Task ClickHeader_RemovesExpandedClass_WhenInitiallyExpanded()
    {
        // Arrange
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.IsExpanded, true)
            .AddChildContent("content"));

        // Act
        await cut.InvokeAsync(() => cut.Find(".header").Click());

        // Assert
        Assert.DoesNotContain("is-expanded", cut.Find(".custom-details").ClassList);
    }

    #endregion

    #region Parameter Update Tests

    [Fact]
    public void ShouldUpdateText_WhenParameterChanges()
    {
        // Arrange
        var cut = Render<Details>(parameters => parameters
            .Add(p => p.Text, "First")
            .AddChildContent("content"));

        // Act
        cut.Render(parameters => parameters
            .Add(p => p.Text, "Second")
            .AddChildContent("content"));

        // Assert
        Assert.Contains("Second", cut.Find(".header-left").TextContent);
    }

    [Fact]
    public void ShouldRenderEmptyText_WhenTextNotProvided()
    {
        // Act
        var cut = Render<Details>(parameters => parameters
            .AddChildContent("content"));

        // Assert - renders without error, only arrow in header
        var headerLeft = cut.Find(".header-left");
        Assert.Equal(string.Empty, headerLeft.TextContent.Trim());
    }

    #endregion
}
