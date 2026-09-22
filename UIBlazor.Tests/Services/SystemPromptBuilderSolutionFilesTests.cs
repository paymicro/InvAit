namespace UIBlazor.Tests.Services;

/// <summary>
/// Tests for <see cref="SystemPromptBuilder.BuildSolutionFiles"/> and <see cref="SystemPromptBuilder.Options"/>.
/// </summary>
public partial class SystemPromptBuilderTests
{
    [Fact]
    public void BuildSolutionFiles_EmptyFileList_ReturnsEmptyString()
    {
        // Arrange
        var context = new VsContext
        {
            SolutionPath = "B:\\TestSolution",
            SolutionFiles = []
        };
        var builder = CreateBuilder();

        // Act
        var result = builder.BuildSolutionFiles(context);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildSolutionFiles_WithRawPaths_FormatsAsTree()
    {
        // Arrange — raw full paths, no emojis, no indentation
        var context = new VsContext
        {
            SolutionPath = "B:\\TestSolution",
            SolutionFiles =
            [
                "B:\\TestSolution\\ConsoleApp\\Program.cs",
                "B:\\TestSolution\\ConsoleApp\\Utils.cs",
                "B:\\TestSolution\\ConsoleApp\\Ui\\Test1.cs",
                "B:\\TestSolution\\ConsoleApp.Tests\\UnitTest.cs",
                "B:\\TestSolution\\Readme.md"
            ],
            SolutionProjects =
            [
                "B:\\TestSolution\\ConsoleApp\\ConsoleApp.csproj",
                "B:\\TestSolution\\ConsoleApp.Tests\\ConsoleApp.Tests.csproj"
            ]
        };
        var builder = CreateBuilder();

        // Act
        var result = builder.BuildSolutionFiles(context);

        // Assert — should contain tree-formatted output with project names
        Assert.Contains("ConsoleApp/", result);
        Assert.Contains("ConsoleApp.Tests/", result);
        Assert.Contains("Program.cs", result);
        Assert.Contains("Utils.cs", result);
        Assert.Contains("Test1.cs", result);
        Assert.Contains("UnitTest.cs", result);
        Assert.Contains("Readme.md", result);
        // Should NOT contain full paths (they are relative to solution)
        Assert.DoesNotContain("B:\\TestSolution", result);
        // Should NOT contain emojis
        Assert.DoesNotContain("📄", result);
        Assert.DoesNotContain("📁", result);
    }

    [Fact]
    public void BuildSolutionFiles_FilesInNestedDirectories_ShowTreeStructure()
    {
        // Arrange
        var context = new VsContext
        {
            SolutionPath = "B:\\TestSolution",
            SolutionFiles =
            [
                "B:\\TestSolution\\src\\File1.cs",
                "B:\\TestSolution\\src\\File2.cs",
                "B:\\TestSolution\\lib\\Lib1.cs"
            ]
        };
        var builder = CreateBuilder();

        // Act
        var result = builder.BuildSolutionFiles(context);

        // Assert
        Assert.Contains("src/", result);
        Assert.Contains("File1.cs", result);
        Assert.Contains("File2.cs", result);
        Assert.Contains("lib/", result);
        Assert.Contains("Lib1.cs", result);
        // Full paths should not appear
        Assert.DoesNotContain("B:\\TestSolution", result);
    }

    [Fact]
    public void Options_ReturnsActiveProfile()
    {
        // Arrange
        var expectedProfile = new ConnectionProfile { SystemPrompt = "Test" };
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(expectedProfile);
        var builder = CreateBuilder();

        // Act
        var result = builder.Options;

        // Assert
        Assert.Same(expectedProfile, result);
    }
}
