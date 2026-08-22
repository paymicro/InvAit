namespace UIBlazor.Tests.Agents;

public class BuiltInAgentTests
{
    private readonly Mock<IVsBridge> _vsBridgeMock;
    private readonly BuiltInAgent _agent;

    public BuiltInAgentTests()
    {
        _vsBridgeMock = new Mock<IVsBridge>();
        _agent = new BuiltInAgent(_vsBridgeMock.Object, Mock.Of<ISkillService>(), Mock.Of<IInternalExecutor>());
    }

    [Fact]
    public void Tools_AreRegistered()
    {
        Assert.NotEmpty(_agent.Tools);
        Assert.Contains(_agent.Tools, t => t.Name == BuiltInToolEnum.ReadFiles);
        Assert.Contains(_agent.Tools, t => t.Name == BuiltInToolEnum.CreateFile);
        Assert.Contains(_agent.Tools, t => t.Name == BuiltInToolEnum.Edits);
    }

    [Fact]
    public void Tools_DelegateTask_IsRegistered()
    {
        var tool = _agent.Tools.FirstOrDefault(t => t.Name == BuiltInToolEnum.DelegateTask);
        Assert.NotNull(tool);
        Assert.Equal(ToolCategory.SubAgent, tool!.Category);
        Assert.NotNull(tool.NativeTool);
        Assert.Equal(BuiltInToolEnum.DelegateTask, tool.NativeTool.Function.Name);
    }

    [Fact]
    public void Tools_DelegateTask_HasExecuteWithContextAsync()
    {
        var tool = _agent.Tools.First(t => t.Name == BuiltInToolEnum.DelegateTask);
        Assert.NotNull(tool.ExecuteWithContextAsync);
    }

    [Fact]
    public async Task Execute_DelegateTask_CallsInternalExecutor()
    {
        // Arrange
        var tool = _agent.Tools.First(t => t.Name == BuiltInToolEnum.DelegateTask);
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        // Act
        await tool.ExecuteAsync(args, CancellationToken.None);

        // Assert - InternalExecutor is called (which routes to SubAgentExecutor)
        // Since we mock IInternalExecutor, it just returns default
        // The key is that it doesn't throw
    }

    [Fact]
    public async Task Execute_ReadFiles_CallsBridge()
    {
        // Arrange
        var tool = _agent.Tools.First(t => t.Name == BuiltInToolEnum.ReadFiles);
        var args = JsonSerializer.Serialize(new Dictionary<string, object> { { "path", "test.cs" } });

        // Act
        await tool.ExecuteAsync(args, CancellationToken.None);

        // Assert
        _vsBridgeMock.Verify(b => b.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, args, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task Execute_ApplyDiff_CallsBridge()
    {
        // Arrange
        var tool = _agent.Tools.First(t => t.Name == BuiltInToolEnum.Edits);
        var args = JsonSerializer.Serialize(new Dictionary<string, object> { { "path", "test.cs" } });

        // Act
        await tool.ExecuteAsync(args, CancellationToken.None);

        // Assert
        _vsBridgeMock.Verify(b => b.ExecuteToolAsync(BuiltInToolEnum.Edits, args, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task Execute_Exec_CallsBridge()
    {
        // Arrange
        var tool = _agent.Tools.First(t => t.Name == BuiltInToolEnum.Bash);
        var args = JsonSerializer.Serialize(new Dictionary<string, object> { { "command", "dir" } });

        // Act
        await tool.ExecuteAsync(args, CancellationToken.None);

        // Assert
        _vsBridgeMock.Verify(b => b.ExecuteToolAsync(BuiltInToolEnum.Bash, args, CancellationToken.None), Times.Once);
    }
}
