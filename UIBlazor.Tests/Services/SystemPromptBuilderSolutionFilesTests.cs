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
        var context = new VsCodeContext
        {
            SolutionPath = "B:\\TestSolution",
            SolutionFiles = []
        };
        var builder = CreateBuilder();

        // Act
        var result = builder.BuildSolutionFiles(context, true);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildSolutionFiles_WithDirectoriesAndFiles_FormatsCorrectly()
    {
        // Arrange
        var context = new VsCodeContext
        {
            SolutionPath = "B:\\TestSolution",
            SolutionFiles = [.. $"""
                Solution path: {VsCodeContext.DirPrefix} B:\TestSolution
                Project: ConsoleApp | B:\TestSolution\ConsoleApp\ConsoleApp.csproj
                {VsCodeContext.FilePrefix} B:\TestSolution\Readme.md
                {VsCodeContext.DirPrefix} B:\TestSolution\ConsoleApp\
                  {VsCodeContext.FilePrefix} B:\TestSolution\ConsoleApp\Program.cs
                  {VsCodeContext.FilePrefix} B:\TestSolution\ConsoleApp\Utils.cs
                {VsCodeContext.DirPrefix} B:\TestSolution\ConsoleApp\Ui\
                  {VsCodeContext.FilePrefix} B:\TestSolution\ConsoleApp\Ui\Test1.cs
                  {VsCodeContext.FilePrefix} B:\Other\ConsoleApp\Ui\Test2.cs
                Project: ConsoleApp.Tests | B:\TestSolution\ConsoleApp.Tests\ConsoleApp.Tests.csproj
                {VsCodeContext.DirPrefix} B:\TestSolution\ConsoleApp.Tests\
                  {VsCodeContext.FilePrefix} B:\TestSolution\ConsoleApp.Tests\UnitTest.cs
                """.Split('\n')]
        };
        var expected = $"""
            Solution path: {VsCodeContext.DirPrefix} B:\TestSolution
            Project: ConsoleApp | ConsoleApp\ConsoleApp.csproj
            {VsCodeContext.FilePrefix} Readme.md
            {VsCodeContext.DirPrefix} B:\TestSolution\ConsoleApp\
              {VsCodeContext.FilePrefix} Program.cs
              {VsCodeContext.FilePrefix} Utils.cs
            {VsCodeContext.DirPrefix} B:\TestSolution\ConsoleApp\Ui\
              {VsCodeContext.FilePrefix} Test1.cs
              {VsCodeContext.FilePrefix} B:\Other\ConsoleApp\Ui\Test2.cs
            Project: ConsoleApp.Tests | ConsoleApp.Tests\ConsoleApp.Tests.csproj
            {VsCodeContext.DirPrefix} B:\TestSolution\ConsoleApp.Tests\
              {VsCodeContext.FilePrefix} UnitTest.cs

            """;
        var builder = CreateBuilder();

        // Act
        var result = builder.BuildSolutionFiles(context, true);

        // Assert
        Assert.Equal(result, expected);
    }

    [Fact]
    public void BuildSolutionFiles_FilesWithoutDirectoryPrefix_AreRelativeToLastDirectory()
    {
        // Arrange
        var context = new VsCodeContext
        {
            SolutionPath = "B:\\TestSolution",
            SolutionFiles =
            [
                $"{VsCodeContext.DirPrefix} B:\\TestSolution\\src",
                "B:\\TestSolution\\src\\File1.cs",
                "B:\\TestSolution\\src\\File2.cs",
                $"{VsCodeContext.DirPrefix} B:\\TestSolution\\lib",
                "B:\\TestSolution\\lib\\Lib1.cs"
            ]
        };
        var builder = CreateBuilder();

        // Act
        var result = builder.BuildSolutionFiles(context, true);

        // Assert
        Assert.Contains("File1.cs", result);
        Assert.Contains("File2.cs", result);
        Assert.Contains("Lib1.cs", result);
        Assert.DoesNotContain("B:\\TestSolution\\src\\File1.cs", result);
        Assert.DoesNotContain("B:\\TestSolution\\lib\\Lib1.cs", result);
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
