using Microsoft.Extensions.AI;
using Moq;
using UIBlazor.Agents;
using UIBlazor.Constants;
using UIBlazor.Models;
using UIBlazor.Services.AgentFramework;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;


// Radzen.Blazor (global usings in test infra) also declares ChatMessage.
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Integration tests for the Phase-2 agent loop (<see cref="AfAgentLoop"/>):
/// the FunctionInvokingChatClient run waits for our AIFunction (tool)
/// invocation to complete before producing the next model turn, i.e. the main agent must
/// not continue while a tool (e.g. delegate_task) is still running.
/// </summary>
public class AfAgentLoopTests
{
    [Fact]
    public async Task Loop_WaitsForToolCompletion_BeforeNextTurn()
    {
        var gate = new TaskCompletionSource();
        var toolStarted = new TaskCompletionSource();
        var doneReceived = new TaskCompletionSource();
        var mockCalls = new List<List<ToolCall>>();

        var handlerMock = new Mock<IToolCallHandler>();
        handlerMock.Setup(h => h.PrepareToolsForApprovals(It.IsAny<List<ToolCall>>()));
        handlerMock
            .Setup(h => h.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns(async (List<ToolCall> calls, AppMode mode, CancellationToken ct) =>
            {
                mockCalls.Add(calls);
                toolStarted.TrySetResult();

                // Simulate a long-running tool (like delegate_task waiting for a sub-agent).
                try
                {
                    await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }

                var call = calls[0];
                call.Result = new ToolResult { Content = "TOOL_DONE", Name = call.Function.Name, Success = true };
            });

        var client = new ScriptedChatClient(
            () => new[]
            {
                new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
                {
                    Contents = [new FunctionCallContent("c1", "test_tool", new Dictionary<string, object?> { ["task"] = "do it" })],
                    FinishReason = ChatFinishReason.ToolCalls
                }
            },
            () => new[]
            {
                new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
                {
                    Contents = [new TextContent("DONE")],
                    FinishReason = ChatFinishReason.Stop
                }
            });

        var loop = CreateLoop(handlerMock.Object, client);
        var session = new ConversationSession { Id = "s", Mode = AppMode.Agent };
        var capture = new CompletionsResult();

        // Drive the stream on a background task so this test can observe the exact
        // ordering between the tool completion and the next model turn.
        var enumerateTask = Task.Run(async () =>
        {
            var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await foreach (var delta in loop
                .GetCompletionsAsync(
                    [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], [Tool()], session, capture, timeoutCts.Token)
                .ConfigureAwait(false))
            {
                if (delta.Content == "DONE")
                    doneReceived.TrySetResult();
            }
        });

        // Wait until the tool actually started running.
        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // The loop must NOT produce the next model turn ("DONE") while the tool
        // is still in flight — the gate is still closed at this point.
        Assert.False(doneReceived.Task.IsCompleted,
            "The loop produced the next turn while the tool was still running");

        // Complete the tool; only now the second model turn may arrive.
        gate.TrySetResult();
        await enumerateTask;

        Assert.True(doneReceived.Task.IsCompleted);
        Assert.True(capture.ToolsExecutedByAgent);

        // The tool was invoked exactly once - progressive updates of the same CallId
        // (partial arguments per chunk) are accumulated and executed once per turn.
        Assert.Single(mockCalls);
        Assert.Equal("TOOL_DONE", mockCalls[0][0].Result?.Content);

        // The accumulator is cleared before each iteration; the final turn had no tool
        // calls, so it stays null after the run (ToolsExecutedByAgent is the flag).
        Assert.Null(capture.AccumulatedToolCalls);
    }

    /// <summary>
    /// ParseArgs fallback at the loop level: the SSE stream carries a tool call whose
    /// arguments JSON is cut mid-string (invalid JSON). The partial is repaired
    /// (JsonUtils.DeserializePartialAnonymousType / RepairJson) and accumulated, and the
    /// assistant message appended to the model history must carry a
    /// <see cref="FunctionCallContent"/> whose Arguments dictionary is NOT null and contains
    /// the repaired keys — the model still sees the partial arguments instead of empty {{}}.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Loop_TruncatedToolCallArguments_RepairedPartialInModelHistory()
    {
        // chunk1 = tool call with arguments cut inside the "content" string value
        // ({"filePath":"a.cs","content":"Hello wor — invalid JSON), chunk2 = finish_reason.
        var sse = """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"filePath\":\"a.cs\",\"content\":\"Hello wor"}}]},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{},"finish_reason":"tool_calls"}],"usage":null}

            data: [DONE]

            """;
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithHeader("Cache-Control", "no-cache")
                .WithBody(sse));

        var handlerMock = new Mock<IToolCallHandler>();
        handlerMock.Setup(h => h.PrepareToolsForApprovals(It.IsAny<List<ToolCall>>()));
        handlerMock
            .Setup(h => h.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns(async (List<ToolCall> calls, AppMode mode, CancellationToken ct) =>
            {
                var call = calls[0];
                call.Result = new ToolResult { Content = "TOOL_DONE", Name = call.Function.Name, Success = true };
                await Task.CompletedTask;
            });

        // Turn 1 streams through the real transport (WireMock SSE); turn 2 records the
        // history the loop assembled and yields the final text.
        var serverClient = AfChatClientFactory.Create(
            new ConnectionProfile { Endpoint = server.Urls[0], ApiKey = "key", Model = "test-model", Stream = true },
            new HttpClient());
        var client = new ServerThenCapturingClient(serverClient);

        var loop = CreateLoop(handlerMock.Object, client);
        var session = new ConversationSession { Id = "s", Mode = AppMode.Agent };
        var capture = new CompletionsResult();

        await foreach (var _ in loop
            .GetCompletionsAsync(
                [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], [Tool()], session, capture, TestContext.Current.CancellationToken)
            .ConfigureAwait(false))
        {
        }

        Assert.True(capture.ToolsExecutedByAgent);
        Assert.Equal(2, client.Turns.Count);

        // The assistant message appended to the history carries the REPAIRED partial
        // arguments (not null, not empty {}).
        var history = client.Turns[1];
        var functionCall = history
            .Single(m => m.Role.Value == "assistant")
            .Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("call_1", functionCall.CallId);
        Assert.Equal("read_file", functionCall.Name);
        Assert.NotNull(functionCall.Arguments);
        Assert.NotEmpty(functionCall.Arguments);
        Assert.True(functionCall.Arguments.ContainsKey("filePath"), "repaired partial arguments must contain filePath");
        Assert.Equal("a.cs", Assert.IsType<JsonElement>(functionCall.Arguments["filePath"]).GetString());

        // The result of the executed call is in the history too.
        var toolMessage = history.Single(m => m.Role.Value == "tool");
        var result = Assert.IsType<FunctionResultContent>(toolMessage.Contents.Single());
        Assert.Equal("call_1", result.CallId);
        Assert.Equal("TOOL_DONE", result.Result?.ToString());
    }

    private static Tool Tool() => new()
    {
        Name = "test_tool",
        NativeTool = new NativeToolDefinition
        {
            Function = new NativeToolFunction
            {
                Name = "test_tool",
                Description = "test tool",
                Strict = true,
                Parameters = new NativeParameters
                {
                    Type = NativeToolType.Object,
                    Properties = new()
                    {
                        ["task"] = new NativePropertyDefinition { Type = NativeToolType.String, Description = "task" }
                    },
                    Required = ["task"]
                }
            }
        },
        ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult())
    };

    private static AfAgentLoop CreateLoop(IToolCallHandler handler, IChatClient client)
        => new(
            () => new ConnectionProfile { Endpoint = "https://api.test", ApiKey = "key", Model = "test-model", Stream = true, Temperature = 0.7f, MaxTokens = 1000 },
            new HttpClient(),
            handler,
            (_, _) => client);

    /// <summary>
    /// IChatClient returning a scripted stream per call (turn 1 = tool call, turn 2 = final text).
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatResponseUpdate>> _turn1;
        private readonly Func<IEnumerable<ChatResponseUpdate>> _turn2;
        private int _call;

        public ScriptedChatClient(
            Func<IEnumerable<ChatResponseUpdate>> turn1,
            Func<IEnumerable<ChatResponseUpdate>> turn2)
        {
            _turn1 = turn1;
            _turn2 = turn2;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var turn = Interlocked.Increment(ref _call);
            if (turn > 2)
                throw new InvalidOperationException($"Unexpected extra model call #{turn}");

            return Enumerate(turn == 1 ? _turn1() : _turn2());
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var turn = Interlocked.Increment(ref _call);
            if (turn > 2)
                throw new InvalidOperationException($"Unexpected extra model call #{turn}");

            var updates = turn == 1 ? _turn1() : _turn2();
            var last = updates.LastOrDefault() ?? throw new InvalidOperationException("no updates");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. last.Contents])));
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> Enumerate(IEnumerable<ChatResponseUpdate> updates)
        {
            foreach (var update in updates)
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Turn 1 streams through the real transport (WireMock SSE); turn 2 records the model
    /// history passed by the loop and yields the final text.
    /// </summary>
    private sealed class ServerThenCapturingClient : IChatClient
    {
        private readonly IChatClient _serverClient;
        private int _turn;

        public ServerThenCapturingClient(IChatClient serverClient) => _serverClient = serverClient;

        public List<IReadOnlyList<ChatMessage>> Turns { get; } = [];

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToList();
            var turn = Interlocked.Increment(ref _turn);
            if (turn > 2)
                throw new InvalidOperationException($"Unexpected extra model call #{turn}");

            Turns.Add(snapshot);
            if (turn == 1)
                return _serverClient.GetStreamingResponseAsync(snapshot, options, cancellationToken);

            return SingleTextUpdate("DONE");
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The loop streams; GetResponseAsync is not used.");

        private static async IAsyncEnumerable<ChatResponseUpdate> SingleTextUpdate(string text)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, text) { FinishReason = ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey) => null;
        public void Dispose() { }
    }
}
