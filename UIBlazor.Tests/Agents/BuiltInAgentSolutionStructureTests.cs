namespace UIBlazor.Tests.Agents;

/// <summary>
/// Tests for the <c>get_solution_structure</c> tool's <see cref="BuiltInAgent.GetSolutionStructureAsync"/>
/// behavior: returns a formatted ASCII tree from cached context when available,
/// falls back to <see cref="IVsBridge"/> when context is null or has no files.
/// </summary>
public class BuiltInAgentSolutionStructureTests
{
    private readonly Mock<IVsBridge> _vsBridgeMock;
    private readonly Mock<IContextService> _contextServiceMock;
    private readonly BuiltInAgent _agent;

    public BuiltInAgentSolutionStructureTests()
    {
        _vsBridgeMock = new Mock<IVsBridge>();
        _contextServiceMock = new Mock<IContextService>();
        _agent = new BuiltInAgent(
            _vsBridgeMock.Object,
            Mock.Of<ISkillService>(MockBehavior.Loose),
            Mock.Of<IInternalExecutor>(MockBehavior.Loose),
            _contextServiceMock.Object);
    }

    /// <summary>
    /// Helper: finds the <c>get_solution_structure</c> tool registered on the agent.
    /// </summary>
    private Tool GetSolutionStructureTool() =>
        _agent.Tools.First(t => t.Name == BuiltInToolEnum.GetSolutionStructure);

    // -----------------------------------------------------------------------
    // 1. Returns formatted tree when context has files
    // -----------------------------------------------------------------------
    [Fact]
    public async Task GetSolutionStructure_WhenContextHasFiles_ReturnsFormattedTree()
    {
        // Arrange — context with a couple of files under a known solution root
        var context = new VsContext
        {
            SolutionPath = "/repo/MyApp",
            SolutionFiles =
            [
                "/repo/MyApp/Program.cs",
                "/repo/MyApp/Utils/Helper.cs"
            ],
            SolutionProjects = []
        };
        _contextServiceMock.SetupGet(c => c.CurrentContext).Returns(context);

        // Act
        var tool = GetSolutionStructureTool();
        var result = await tool.ExecuteAsync(null, TestContext.Current.CancellationToken);

        // Assert — the result should be a formatted ASCII tree, not a bridge call
        Assert.True(result.Success);
        Assert.Equal(BuiltInToolEnum.GetSolutionStructure, result.Name);
        Assert.Contains("├─", result.Result);
        Assert.Contains("└─", result.Result);
        Assert.Contains("Program.cs", result.Result);
        Assert.Contains("Helper.cs", result.Result);

        // Bridge should NOT have been called
        _vsBridgeMock.Verify(
            b => b.ExecuteToolAsync(BuiltInToolEnum.GetSolutionStructure, null, TestContext.Current.CancellationToken),
            Times.Never);
    }

    // -----------------------------------------------------------------------
    // 2. Falls back to VsBridge when context is null
    // -----------------------------------------------------------------------
    [Fact]
    public async Task GetSolutionStructure_WhenContextIsNull_FallsBackToVsBridge()
    {
        // Arrange
        _contextServiceMock.SetupGet(c => c.CurrentContext).Returns((VsContext?)null);

        var bridgeResult = new VsToolResult
        {
            Name = BuiltInToolEnum.GetSolutionStructure,
            Result = "bridge-flat-list",
            Success = true
        };
        _vsBridgeMock
            .Setup(b => b.ExecuteToolAsync(BuiltInToolEnum.GetSolutionStructure, null, TestContext.Current.CancellationToken))
            .ReturnsAsync(bridgeResult);

        // Act
        var tool = GetSolutionStructureTool();
        var result = await tool.ExecuteAsync(null, TestContext.Current.CancellationToken);

        // Assert — the exact bridge result is returned
        Assert.Same(bridgeResult, result);
        Assert.Equal("bridge-flat-list", result.Result);
        Assert.True(result.Success);

        _vsBridgeMock.Verify(
            b => b.ExecuteToolAsync(BuiltInToolEnum.GetSolutionStructure, null, TestContext.Current.CancellationToken),
            Times.Once);
    }

    // -----------------------------------------------------------------------
    // 3. Falls back to VsBridge when SolutionFiles is empty
    // -----------------------------------------------------------------------
    [Fact]
    public async Task GetSolutionStructure_WhenSolutionFilesEmpty_FallsBackToVsBridge()
    {
        // Arrange — context exists but has no files
        var context = new VsContext
        {
            SolutionPath = "/repo/MyApp",
            SolutionFiles = [],
            SolutionProjects = []
        };
        _contextServiceMock.SetupGet(c => c.CurrentContext).Returns(context);

        var bridgeResult = new VsToolResult
        {
            Name = BuiltInToolEnum.GetSolutionStructure,
            Result = "bridge-empty-context",
            Success = true
        };
        _vsBridgeMock
            .Setup(b => b.ExecuteToolAsync(BuiltInToolEnum.GetSolutionStructure, null, TestContext.Current.CancellationToken))
            .ReturnsAsync(bridgeResult);

        // Act
        var tool = GetSolutionStructureTool();
        var result = await tool.ExecuteAsync(null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(bridgeResult, result);
        Assert.Equal("bridge-empty-context", result.Result);

        _vsBridgeMock.Verify(
            b => b.ExecuteToolAsync(BuiltInToolEnum.GetSolutionStructure, null, TestContext.Current.CancellationToken),
            Times.Once);
    }

    // -----------------------------------------------------------------------
    // 4. Formatted result uses project names instead of directory names
    // -----------------------------------------------------------------------
    [Fact]
    public async Task GetSolutionStructure_WhenProjectPathProvided_RenamesDirectoryToProjectName()
    {
        // Arrange — the directory "src" contains "MyCoolApp.csproj", so the
        // tree builder should rename the "src" directory node to "MyCoolApp".
        var context = new VsContext
        {
            SolutionPath = "/repo/Solution",
            SolutionFiles =
            [
                "/repo/Solution/src/Program.cs"
            ],
            SolutionProjects =
            [
                "/repo/Solution/src/MyCoolApp.csproj"
            ]
        };
        _contextServiceMock.SetupGet(c => c.CurrentContext).Returns(context);

        // Act
        var tool = GetSolutionStructureTool();
        var result = await tool.ExecuteAsync(null, TestContext.Current.CancellationToken);

        // Assert — the project name should appear, the raw directory name should not
        Assert.True(result.Success);
        Assert.Contains("MyCoolApp", result.Result);
        Assert.DoesNotContain("src/", result.Result);
        Assert.Contains("Program.cs", result.Result);

        // Bridge should NOT have been called
        _vsBridgeMock.Verify(
            b => b.ExecuteToolAsync(BuiltInToolEnum.GetSolutionStructure, null, TestContext.Current.CancellationToken),
            Times.Never);
    }
}
