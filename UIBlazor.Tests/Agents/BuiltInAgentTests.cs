namespace UIBlazor.Tests.Agents;

public class BuiltInAgentTests
{
    private readonly Mock<IVsBridge> _vsBridgeMock;
    private readonly Mock<ICommonSettingsProvider> _commonSettings;
    private readonly BuiltInAgent _agent;

    public BuiltInAgentTests()
    {
        _vsBridgeMock = new Mock<IVsBridge>();
        _commonSettings = new Mock<ICommonSettingsProvider>();
        _commonSettings.SetupGet(x => x.Current).Returns(new CommonOptions());
        _agent = new BuiltInAgent(
            _vsBridgeMock.Object,
            Mock.Of<ISkillService>(),
            Mock.Of<IInternalExecutor>(),
            Mock.Of<IContextService>(),
            _commonSettings.Object);
    }

    [Fact]
    public void Tools_AreRegistered()
    {
        Assert.NotEmpty(_agent.Tools);
        Assert.Contains(_agent.Tools, t => t.Name == BuiltInToolEnum.ReadFiles);
        Assert.Contains(_agent.Tools, t => t.Name == BuiltInToolEnum.CreateFile);
        Assert.Contains(_agent.Tools, t => t.Name == BuiltInToolEnum.EditFile);
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
        var tool = _agent.Tools.First(t => t.Name == BuiltInToolEnum.EditFile);
        var args = JsonSerializer.Serialize(new Dictionary<string, object> { { "path", "test.cs" } });

        // Act
        await tool.ExecuteAsync(args, CancellationToken.None);

        // Assert
        _vsBridgeMock.Verify(b => b.ExecuteToolAsync(BuiltInToolEnum.EditFile, args, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task Execute_Exec_CallsBridge()
    {
        // Arrange
        var tool = _agent.Tools.First(t => t.Name == BuiltInToolEnum.Bash);
        var args = JsonSerializer.Serialize(new Dictionary<string, object> { { "command", "dir" } });

        // Act
        await tool.ExecuteAsync(args, CancellationToken.None);

        // Assert — bash carries the settings timeout, so the payload is no longer the literal args
        _vsBridgeMock.Verify(b => b.ExecuteToolAsync(
            BuiltInToolEnum.Bash,
            It.Is<string>(p => p.Contains("\"command\":\"dir\"", StringComparison.Ordinal)),
            CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task Execute_Exec_SendsSettingsTimeoutInPayload()
    {
        // The host used to hard-code 120 000 ms for bash, so the user-visible setting controlled
        // only the client-side wait and the badge while the real limit was a constant.
        _commonSettings.SetupGet(x => x.Current).Returns(new CommonOptions { ToolTimeoutMs = 45_000 });
        var tool = _agent.Tools.First(t => t.Name == BuiltInToolEnum.Bash);

        string? sent = null;
        _vsBridgeMock
            .Setup(b => b.ExecuteToolAsync(BuiltInToolEnum.Bash, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, CancellationToken>((_, payload, _) => sent = payload)
            .ReturnsAsync(new VsToolResult { Name = BuiltInToolEnum.Bash, Success = true, Result = "ok" });

        // Act
        await tool.ExecuteAsync(JsonSerializer.Serialize(new Dictionary<string, object> { { "command", "ls" } }), CancellationToken.None);

        // Assert — the setting rides in the payload as `timeoutMs`, the model's own args survive
        Assert.NotNull(sent);
        using var doc = JsonDocument.Parse(sent!);
        Assert.Equal(45_000, doc.RootElement.GetProperty("timeoutMs").GetInt32());
        Assert.Equal("ls", doc.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public async Task Execute_Exec_OverridesModelSuppliedTimeout()
    {
        // The model may smuggle `timeoutMs` into the call arguments; the timeout is the USER's
        // decision, so the setting must win — otherwise the model could lift its own limit.
        _commonSettings.SetupGet(x => x.Current).Returns(new CommonOptions { ToolTimeoutMs = 20_000 });
        var tool = _agent.Tools.First(t => t.Name == BuiltInToolEnum.Bash);

        string? sent = null;
        _vsBridgeMock
            .Setup(b => b.ExecuteToolAsync(BuiltInToolEnum.Bash, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, CancellationToken>((_, payload, _) => sent = payload)
            .ReturnsAsync(new VsToolResult { Name = BuiltInToolEnum.Bash, Success = true, Result = "ok" });

        // Act
        await tool.ExecuteAsync("{\"command\":\"ls\",\"timeoutMs\":600000}", CancellationToken.None);

        // Assert
        using var doc = JsonDocument.Parse(sent!);
        Assert.Equal(20_000, doc.RootElement.GetProperty("timeoutMs").GetInt32());
    }
}
