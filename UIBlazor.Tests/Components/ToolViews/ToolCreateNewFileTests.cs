namespace UIBlazor.Tests.Components.ToolViews;

/// <summary>
/// Tests for <see cref="ToolCreateNewFile"/>
/// </summary>
public class ToolCreateNewFileTests : BunitContext
{
    public ToolCreateNewFileTests()
    {
        Services.AddRadzenComponents();
    }

    [Fact]
    public void ShouldRenderDiffViewContainer()
    {
        // Act
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "C:\\src\\Program.cs")
            .Add(p => p.Content, "Console.WriteLine();"));

        // Assert
        Assert.NotNull(cut.Find(".diff-view"));
        Assert.NotNull(cut.Find(".diff-block"));
    }

    [Fact]
    public void ShouldRenderFilePathHeader()
    {
        // Act
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "C:\\src\\MyService.cs")
            .Add(p => p.Content, "code"));

        // Assert
        var header = cut.Find(".tool-file-header");
        Assert.Equal("C:\\src\\MyService.cs", header.TextContent);
    }

    [Fact]
    public void ShouldNotRenderFilePathHeader_WhenFilePathIsEmpty()
    {
        // Act
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, string.Empty)
            .Add(p => p.Content, "code"));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-file-header"));
    }

    [Fact]
    public void ShouldRenderContent_InPreElement()
    {
        // Arrange
        var content = "namespace Demo;\n\npublic class Demo { }";

        // Act
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.Content, content));

        // Assert
        var pre = cut.Find(".diff-block pre");
        Assert.Contains(content, pre.TextContent);
    }

    [Fact]
    public void ShouldRenderEmptyPre_WhenContentNotProvided()
    {
        // Act
        var cut = Render<ToolCreateNewFile>();

        // Assert - renders without error
        Assert.NotNull(cut.Find(".diff-block pre"));
    }

    [Fact]
    public void ShouldUpdateContent_WhenParameterChanges()
    {
        // Arrange
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.Content, "old content"));

        // Act
        cut.Render(parameters => parameters
            .Add(p => p.Content, "new content"));

        // Assert
        Assert.Contains("new content", cut.Find(".diff-block pre").TextContent);
    }
}
