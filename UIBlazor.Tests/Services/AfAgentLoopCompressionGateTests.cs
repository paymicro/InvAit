using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Mid-run compression in the MAIN agent loop (todo.md: «сжатие сессии не вызывается при
/// достижении лимита в какой-то новый тур»): the start-of-run check in
/// AIChat.GetAiResponseInternalAsync only covers the first LLM call, while a tool-heavy run
/// crosses the token threshold in a LATER turn. The loop must check the compression gate at
/// every iteration boundary (AfSubAgentOrchestrator parity, which checks before every attempt)
/// via <see cref="AfAgentLoop.CompressionGate"/> and re-base its interject-scan baseline after
/// compression rewrites the session history.
/// </summary>
public class AfAgentLoopCompressionGateTests
{
    [Fact(Timeout = 20000)]
    public async Task Loop_CompressionGate_CalledAtEveryTurnBoundary_AndRebasesInterjectBaseline()
    {
        var compressCalls = 0;
        // RunContinuationsAsynchronously: без него продолжение теста инлайнится ВНУТРЬ вызова
        // гейта — interject добавится до возврата baseline, и гейт вернёт count уже с ним
        // (скан пропустит сообщение).
        var compressionDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handlerMock = new Mock<IToolCallHandler>();
        handlerMock.Setup(h => h.PrepareToolsForApprovals(It.IsAny<List<ToolCall>>()));
        handlerMock
            .Setup(h => h.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns((List<ToolCall> calls, AppMode mode, CancellationToken ct) =>
            {
                foreach (var call in calls)
                    call.Result = new ToolResult { Content = "TOOL_DONE", Name = call.Function.Name, Success = true };
                return Task.CompletedTask;
            });

        // Turn 1: tool_call (threshold crossed while it runs). Turn 2: empty text turn —
        // the gate must fire before it. Turn 3: user interject must be picked up (the
        // baseline was re-based by the gate after compression rewrote the history).
        var client = new ScriptedChatClient(
            () =>
            [
                new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
                {
                    Contents = [new FunctionCallContent("c1", "test_tool", new Dictionary<string, object?> { ["task"] = "do it" })],
                    FinishReason = ChatFinishReason.ToolCalls
                }
            ],
            () =>
            [
                new ChatResponseUpdate(ChatRole.Assistant, "PARTIAL") { FinishReason = ChatFinishReason.Stop }
            ]);

        client.Turn2Gate = new TaskCompletionSource();

        var session = new ConversationSession { Id = "s", Mode = AppMode.Agent };
        var loop = new AfAgentLoop(
            () => new ConnectionProfile { Endpoint = "https://api.test", ApiKey = "key", Model = "test-model", Stream = true, Temperature = 0.7f, MaxTokens = 1000 },
            new HttpClient(),
            handlerMock.Object,
            (_, _) => client)
        {
            CompressionGate = (targetSession, _) =>

            {
                compressCalls++;
                if (compressCalls == 2)
                {
                    // The gate is checked at EVERY iteration boundary (iteration 0 included —
                    // orchestrator parity), so call #2 is the boundary between turn 1's tool
                    // run and turn 2. Simulate compression: the token threshold was crossed
                    // during turn 1's tool run and the history was rewritten (SetMessages).
                    // The returned baseline is the count AT COMPRESSION TIME — interjects
                    // arriving after compression must be picked up by the turn-boundary scan.
                    targetSession.SetMessages(targetSession.GetMessagesSnapshot());
                    compressionDone.TrySetResult();
                }
                return Task.FromResult(targetSession.GetMessageCount());
            }
        };

        var capture = new CompletionsResult();

        var enumerateTask = Task.Run(async () =>
        {
            var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await foreach (var _ in loop
                .GetCompletionsAsync(
                    [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], [Tool()], session, capture, timeoutCts.Token)
                .ConfigureAwait(false))
            {
            }
        });

        // Act: the user interjects while turn 2 is still streaming (the stream holds on
        // Turn2Gate) — deterministic ordering, the turn-2 boundary scan must pick it up.
        await compressionDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.AddMessage(new VisualChatMessage { Role = ChatMessageRole.User, Content = "USER_INTERJECT" });
        client.Turn2Gate!.TrySetResult();

        await enumerateTask;

        // Assert: the gate ran at every iteration boundary (iteration 0, after turn 1's tool
        // run, after turn 2's text).
        Assert.True(compressCalls >= 3, $"compression gate ran {compressCalls} time(s); expected >= 3 (every turn boundary)");

        // The turn-3 model call must see the interject appended after the tool result —
        // the baseline returned by the gate survived the compression rewrite.
        Assert.Equal(3, client.Turns.Count);
        var history = client.Turns[2].ToList();
        Assert.Equal("USER_INTERJECT", history.Last(m => m.Role.Value == "user").Text);
        Assert.Contains(history, m => m.Role.Value == "tool");
    }

    [Fact(Timeout = 20000)]
    public async Task Loop_CompressionGateNull_NoCalls()
    {
        var handlerMock = new Mock<IToolCallHandler>();
        handlerMock.Setup(h => h.PrepareToolsForApprovals(It.IsAny<List<ToolCall>>()));
        handlerMock
            .Setup(h => h.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns((List<ToolCall> calls, AppMode mode, CancellationToken ct) =>
            {
                foreach (var call in calls)
                    call.Result = new ToolResult { Content = "TOOL_DONE", Name = call.Function.Name, Success = true };
                return Task.CompletedTask;
            });

        var client = new ScriptedChatClient(
            () =>
            [
                new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
                {
                    Contents = [new FunctionCallContent("c1", "test_tool", new Dictionary<string, object?> { ["task"] = "do it" })],
                    FinishReason = ChatFinishReason.ToolCalls
                }
            ],
            () =>
            [
                new ChatResponseUpdate(ChatRole.Assistant, "DONE") { FinishReason = ChatFinishReason.Stop }
            ]);

        var loop = new AfAgentLoop(
            () => new ConnectionProfile { Endpoint = "https://api.test", ApiKey = "key", Model = "test-model", Stream = true, Temperature = 0.7f, MaxTokens = 1000 },
            new HttpClient(),
            handlerMock.Object,
            (_, _) => client);

        var session = new ConversationSession { Id = "s", Mode = AppMode.Agent };
        var capture = new CompletionsResult();

        await foreach (var _ in loop
            .GetCompletionsAsync(
                [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], [Tool()], session, capture, TestContext.Current.CancellationToken)
            .ConfigureAwait(false))
        {
        }

        // No gate wired: the run completes normally (turn 2 answered), nothing to assert
        // beyond "no exception" — the guard is the null-check itself.
        Assert.Equal(2, client.Turns.Count);
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

    /// <summary>
    /// Two-turn scripted client (turn 1 = tool call, turn 2 = final text), recording the
    /// history snapshot passed to each model call. Turn 2 optionally holds after yielding
    /// its update until <see cref="Turn2Gate"/> is released (lets the test interject while
    /// the loop is still streaming, deterministically).
    /// </summary>
    private sealed class ScriptedChatClient(
        Func<IEnumerable<ChatResponseUpdate>> turn1,
        Func<IEnumerable<ChatResponseUpdate>> turn2) : IChatClient
    {
        private int _call;

        public TaskCompletionSource? Turn2Gate { get; set; }

        public List<IReadOnlyList<ChatMessage>> Turns { get; } = [];

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var turn = Interlocked.Increment(ref _call);
            Turns.Add(messages.ToList());
            if (turn > 2)
                return SingleText("DONE"); // turn 3+: interject turn — plain text answer

            return Enumerate(turn == 1 ? turn1() : turn2(), turn == 2 ? Turn2Gate : null);
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The loop streams; GetResponseAsync is not used.");

        private static async IAsyncEnumerable<ChatResponseUpdate> Enumerate(
            IEnumerable<ChatResponseUpdate> updates, TaskCompletionSource? gate)
        {
            await Task.Yield();
            foreach (var update in updates)
            {
                yield return update;
                if (gate is not null)
                    await gate.Task;
            }
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> SingleText(string text)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text) { FinishReason = ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey) => null;
        public void Dispose() { }
    }
}
