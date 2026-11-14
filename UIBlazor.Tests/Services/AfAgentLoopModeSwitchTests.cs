using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Mid-run mode switch: <c>switch_mode</c> mutates <see cref="ConversationSession.Mode"/> while
/// the loop is running, but the system prompt and the enabled tool set are captured at run start.
/// Without a refresh the model keeps the OLD instructions and the OLD tools for the rest of the
/// run — the observed bug "the agent switched the mode, but nothing changed: no new prompt, no
/// new tools". The loop must rebuild both before the next LLM call.
/// </summary>
public class AfAgentLoopModeSwitchTests
{
    /// <summary>Records the system prompt and tool names of every model call.</summary>
    private sealed class RecordingChatClient(params Func<IEnumerable<ChatResponseUpdate>>[] turns) : IChatClient
    {
        private int _call;

        public List<string> SystemPrompts { get; } = [];
        public List<List<string>> ToolNames { get; } = [];

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var turn = Interlocked.Increment(ref _call);
            SystemPrompts.Add(messages.First(m => m.Role == ChatRole.System).Text ?? string.Empty);
            ToolNames.Add([.. (options?.Tools ?? []).Select(t => t.Name)]);

            if (turn > turns.Length)
                throw new InvalidOperationException($"Unexpected extra model call #{turn}");

            return Enumerate(turns[turn - 1]());
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private static async IAsyncEnumerable<ChatResponseUpdate> Enumerate(IEnumerable<ChatResponseUpdate> updates)
        {
            foreach (var update in updates)
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey) => null;
        public void Dispose() { }
    }

    private static Tool MakeTool(string name) => new()
    {
        Name = name,
        NativeTool = new NativeToolDefinition
        {
            Function = new NativeToolFunction
            {
                Name = name,
                Description = name,
                Strict = true,
                Parameters = new NativeParameters
                {
                    Type = NativeToolType.Object,
                    Properties = new() { ["mode"] = new NativePropertyDefinition { Type = NativeToolType.String, Description = "mode" } },
                    Required = ["mode"]
                }
            }
        },
        ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult())
    };

    private static Func<IEnumerable<ChatResponseUpdate>> ToolCallTurn(string toolName, string args)
        => () =>
        [
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                Contents = [new FunctionCallContent("c1", toolName, JsonSerializer.Deserialize<Dictionary<string, object?>>(args))],
                FinishReason = ChatFinishReason.ToolCalls
            }
        ];

    private static Func<IEnumerable<ChatResponseUpdate>> TextTurn(string text)
        => () =>
        [
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                Contents = [new TextContent(text)],
                FinishReason = ChatFinishReason.Stop
            }
        ];

    private static AfAgentLoop CreateLoop(IToolCallHandler handler, IChatClient client)
        => new(
            () => new ConnectionProfile
            {
                Endpoint = "https://api.test",
                ApiKey = "key",
                Model = "test-model",
                Stream = true,
                Temperature = 0.7f,
                MaxTokens = 1000
            },
            new HttpClient(),
            handler,
            (_, _) => client);

    [Fact(Timeout = 20000)]
    public async Task Loop_ModeSwitchedByTool_RebuildsSystemPromptAndToolsBeforeNextTurn()
    {
        // Arrange - turn 1 calls switch_mode; the handler applies it (as InternalExecutor does).
        var session = new ConversationSession { Id = "s", Mode = AppMode.Chat };
        var handler = new Mock<IToolCallHandler>();
        handler.Setup(h => h.PrepareToolsForApprovals(It.IsAny<List<ToolCall>>()));
        handler
            .Setup(h => h.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns((List<ToolCall> calls, AppMode _, CancellationToken _) =>
            {
                session.Mode = AppMode.Plan; // what switch_mode does
                calls[0].Result = new ToolResult { Content = "Switched to Plan", Success = true };
                return Task.CompletedTask;
            });

        var client = new RecordingChatClient(
            ToolCallTurn("switch_mode", """{"mode":"Plan"}"""),
            TextTurn("PLAN_DONE"));

        var loop = CreateLoop(handler.Object, client);
        loop.ModeContextProvider = (mode, _) => Task.FromResult(mode == AppMode.Plan
            ? new ModeContext("PROMPT:Plan", [MakeTool("read_file")])
            : new ModeContext("PROMPT:Chat", [MakeTool("write_file")]));

        // Act
        await foreach (var _ in loop.GetCompletionsAsync(
            [
                new WireMessage { Role = ChatMessageRole.System, Content = "PROMPT:Chat" },
                new WireMessage { Role = ChatMessageRole.User, Content = "hi" }
            ],
            [MakeTool("write_file")],
            session,
            new CompletionsResult(),
            TestContext.Current.CancellationToken))
        {
        }

        // Assert - turn 1 used the original context, turn 2 the refreshed one.
        Assert.Equal(2, client.SystemPrompts.Count);
        Assert.Equal("PROMPT:Chat", client.SystemPrompts[0]);
        Assert.Equal("PROMPT:Plan", client.SystemPrompts[1]);
        Assert.Equal(["write_file"], client.ToolNames[0]);
        Assert.Equal(["read_file"], client.ToolNames[1]);
    }

    [Fact(Timeout = 20000)]
    public async Task Loop_ModeUnchanged_DoesNotRefreshContext()
    {
        // Arrange - no mode switch: the provider must not be consulted at all (no extra work,
        // no chance to drift from the run-start context).
        var session = new ConversationSession { Id = "s", Mode = AppMode.Agent };
        var handler = new Mock<IToolCallHandler>();
        handler.Setup(h => h.PrepareToolsForApprovals(It.IsAny<List<ToolCall>>()));
        handler
            .Setup(h => h.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns((List<ToolCall> calls, AppMode _, CancellationToken _) =>
            {
                calls[0].Result = new ToolResult { Content = "ok", Success = true };
                return Task.CompletedTask;
            });

        var client = new RecordingChatClient(ToolCallTurn("test_tool", """{"mode":"x"}"""), TextTurn("DONE"));
        var loop = CreateLoop(handler.Object, client);
        var providerCalls = 0;
        loop.ModeContextProvider = (_, _) =>
        {
            providerCalls++;
            return Task.FromResult(new ModeContext("PROMPT:rebuilt", [MakeTool("other_tool")]));
        };

        // Act
        await foreach (var _ in loop.GetCompletionsAsync(
            [
                new WireMessage { Role = ChatMessageRole.System, Content = "PROMPT:Agent" },
                new WireMessage { Role = ChatMessageRole.User, Content = "hi" }
            ],
            [MakeTool("test_tool")],
            session,
            new CompletionsResult(),
            TestContext.Current.CancellationToken))
        {
        }

        // Assert
        Assert.Equal(0, providerCalls);
        Assert.All(client.SystemPrompts, prompt => Assert.Equal("PROMPT:Agent", prompt));
    }
}
