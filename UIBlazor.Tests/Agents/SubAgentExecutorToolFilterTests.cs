namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_DelegateTask_AlwaysExcludedFromSubAgentTools()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        var tools = new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.DelegateTask, ToolCategory.SubAgent),
            CreateTool(BuiltInToolEnum.Grep)
        };
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(tools);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - verify GetCompletionsForSubAgentAsync was called with tools that exclude delegate_task
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.Is<IEnumerable<Tool>>(t => !t.Any(tool => tool.Name == BuiltInToolEnum.DelegateTask)),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_AllowedTools_FiltersToWhitelist()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new
        {
            task = "Test",
            systemPrompt = "Prompt",
            allowedTools = new[] { BuiltInToolEnum.ReadFiles, BuiltInToolEnum.Grep }
        });
        SetupChatServiceToReturnContent("Done!");

        var tools = new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.Grep),
            CreateTool(BuiltInToolEnum.Bash),
            CreateTool(BuiltInToolEnum.DelegateTask, ToolCategory.SubAgent)
        };
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(tools);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.Is<IEnumerable<Tool>>(t =>
                    t.Any(tool => tool.Name == BuiltInToolEnum.ReadFiles) &&
                    t.Any(tool => tool.Name == BuiltInToolEnum.Grep) &&
                    !t.Any(tool => tool.Name == BuiltInToolEnum.Bash) &&
                    !t.Any(tool => tool.Name == BuiltInToolEnum.DelegateTask)),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyAllowedTools_Null_TreatsAsAllTools()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new
        {
            task = "Test",
            systemPrompt = "Prompt",
            allowedTools = Array.Empty<string>()
        });
        SetupChatServiceToReturnContent("Done!");

        var tools = new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.Bash),
            CreateTool(BuiltInToolEnum.DelegateTask, ToolCategory.SubAgent)
        };
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(tools);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - empty allowedTools = all tools (except delegate_task)
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.Is<IEnumerable<Tool>>(t =>
                    t.Any(tool => tool.Name == BuiltInToolEnum.ReadFiles) &&
                    t.Any(tool => tool.Name == BuiltInToolEnum.Bash) &&
                    !t.Any(tool => tool.Name == BuiltInToolEnum.DelegateTask)),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_AllowedTools_NullInJson_TreatedAsAllTools()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = "{\"task\":\"Test\",\"systemPrompt\":\"Prompt\",\"allowedTools\":null}";
        SetupChatServiceToReturnContent("Done!");

        var tools = new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.Grep)
        };
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(tools);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - null allowedTools means all tools are available
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.Is<IEnumerable<Tool>>(t =>
                    t.Any(tool => tool.Name == BuiltInToolEnum.ReadFiles) &&
                    t.Any(tool => tool.Name == BuiltInToolEnum.Grep)),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_AllowedTools_ArrayWithEmptyStrings_FiltersOutEmptyStrings()
    {
        // Arrange
        var toolCall = new ToolCall();
        // JSON array with empty strings and whitespace
        var args = "{\"task\":\"Test\",\"systemPrompt\":\"Prompt\",\"allowedTools\":[\"read_files\",\"\",\"   \",\"grep\"]}";
        SetupChatServiceToReturnContent("Done!");

        var tools = new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.Grep),
            CreateTool(BuiltInToolEnum.Bash)
        };
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(tools);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - empty/whitespace strings should be filtered out, only read_files and grep allowed
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.Is<IEnumerable<Tool>>(t =>
                    t.Any(tool => tool.Name == BuiltInToolEnum.ReadFiles) &&
                    t.Any(tool => tool.Name == BuiltInToolEnum.Grep) &&
                    !t.Any(tool => tool.Name == BuiltInToolEnum.Bash)),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_AllowedTools_NonArrayValue_TreatedAsNull()
    {
        // Arrange
        var toolCall = new ToolCall();
        // allowedTools is a string, not an array — should be treated as null (all tools)
        var args = "{\"task\":\"Test\",\"systemPrompt\":\"Prompt\",\"allowedTools\":\"read_files\"}";
        SetupChatServiceToReturnContent("Done!");

        var tools = new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.Grep)
        };
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(tools);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - non-array value should be treated as null, so all tools available
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.Is<IEnumerable<Tool>>(t =>
                    t.Any(tool => tool.Name == BuiltInToolEnum.ReadFiles) &&
                    t.Any(tool => tool.Name == BuiltInToolEnum.Grep)),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_AllowedTools_EmptyArray_TreatedAsAllTools()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = "{\"task\":\"Test\",\"systemPrompt\":\"Prompt\",\"allowedTools\":[]}";
        SetupChatServiceToReturnContent("Done!");

        var tools = new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.Grep)
        };
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(tools);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - empty array = null = all tools available
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.Is<IEnumerable<Tool>>(t =>
                    t.Any(tool => tool.Name == BuiltInToolEnum.ReadFiles) &&
                    t.Any(tool => tool.Name == BuiltInToolEnum.Grep)),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_AllowedTools_ExcludesNonWhitelisted()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new
        {
            task = "Test",
            systemPrompt = "Prompt",
            allowedTools = new[] { BuiltInToolEnum.ReadFiles, BuiltInToolEnum.Grep }
        });
        SetupChatServiceToReturnContent("Done!");

        var tools = new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.Bash),
            CreateTool(BuiltInToolEnum.Grep),
            CreateTool(BuiltInToolEnum.Dir)
        };
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(tools);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - allowedTools limits to {read_files, grep}, all others denied
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.Is<IEnumerable<Tool>>(t =>
                    t.Any(tool => tool.Name == BuiltInToolEnum.ReadFiles) &&
                    t.Any(tool => tool.Name == BuiltInToolEnum.Grep) &&
                    !t.Any(tool => tool.Name == BuiltInToolEnum.Bash) &&
                    !t.Any(tool => tool.Name == BuiltInToolEnum.Dir)),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }
}
