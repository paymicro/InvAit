using System.Runtime.CompilerServices;

namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    private readonly Mock<IChatService> _chatServiceMock;
    private readonly Mock<IToolManager> _toolManagerMock;
    private readonly Mock<ISystemPromptBuilder> _systemPromptBuilderMock;
    private readonly Mock<IProfileManager> _profileManagerMock;
    private readonly Mock<IRetryHandler> _retryHandlerMock;
    private readonly Mock<ILogger<SubAgentExecutor>> _loggerMock;
    private readonly SubAgentExecutor _executor;

    public SubAgentExecutorTests()
    {
        _chatServiceMock = new Mock<IChatService>();
        _toolManagerMock = new Mock<IToolManager>();
        _systemPromptBuilderMock = new Mock<ISystemPromptBuilder>();
        _profileManagerMock = new Mock<IProfileManager>();
        _retryHandlerMock = new Mock<IRetryHandler>();
        _loggerMock = new Mock<ILogger<SubAgentExecutor>>();

        // SystemPromptBuilder returns the custom prompt as-is (no context in tests)
        _systemPromptBuilderMock
            .Setup(x => x.PrepareSubAgentSystemPromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string prompt, CancellationToken _) => string.IsNullOrEmpty(prompt)
                ? "You are a helpful assistant. Complete the task given to you."
                : prompt);

        // ProfileManager returns a profile with compression disabled by default
        _profileManagerMock
            .Setup(x => x.ActiveProfile)
            .Returns(new ConnectionProfile { TokensToCompress = 0 });

        // RetryHandler returns 0 delay so tests don't wait
        _retryHandlerMock
            .Setup(x => x.GetRetryDelay(It.IsAny<int>()))
            .Returns(0);

        _executor = new SubAgentExecutor(
            _chatServiceMock.Object,
            _toolManagerMock.Object,
            _systemPromptBuilderMock.Object,
            _profileManagerMock.Object,
            _retryHandlerMock.Object,
            _loggerMock.Object);
    }

    private void SetupChatServiceToReturnContent(string content)
    {
        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, string, IEnumerable<Tool>, CompletionsResult, CancellationToken>(
                (_, _, _, resultCapture, _) =>
                {
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls = null;
                })
            .Returns(CreateEmptyDeltaStream());
        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                (msg, _, onContent, _, _, _, _) =>
                {
                    msg.Content = content;
                    onContent?.Invoke(content);
                })
            .Returns(Task.CompletedTask);
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());
    }

    private void SetupChatServiceToThrowCancellation(CancellationToken token)
    {
        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateEmptyDeltaStream());
        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Throws(new OperationCanceledException(token));
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());
    }

    private void SetupChatServiceToThrowException(Exception ex)
    {
        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateEmptyDeltaStream());
        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Throws(ex);
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());
    }

    private static async IAsyncEnumerable<ChatDelta> CreateEmptyDeltaStream(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static Tool CreateTool(string name, ToolCategory category = ToolCategory.ReadFiles) => new()
    {
        Name = name,
        DisplayName = name,
        Category = category,
        NativeTool = new NativeToolDefinition
        {
            Function = new NativeToolFunction
            {
                Name = name,
                Description = $"Test tool {name}",
                Parameters = new NativeParameters
                {
                    Type = NativeToolType.Object,
                    Properties = new Dictionary<string, NativePropertyDefinition>(),
                    Required = new List<string>()
                }
            }
        },
        ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true, Result = "ok" })
    };
}
