using Microsoft.Extensions.AI;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Regression tests for progressive streaming tool calls through the own Chat Completions
/// transport (<see cref="AfChatClientFactory"/> → AfChatCompletionsClient) and the shared
/// delta mapper (<see cref="AfDeltaMapper"/> + <see cref="AfUsageHelpers.AccumulateToolCall"/>).
///
/// The former OpenAI SDK bridge buffered argument fragments and yielded the tool call as ONE
/// fully-formed <see cref="FunctionCallContent"/> in a single update emitted after the chunk
/// carrying finish_reason. The own transport emits PROGRESSIVELY: one ChatResponseUpdate per
/// SSE chunk — the first chunk carries id + name (arguments not yet parseable), every argument
/// fragment produces an intermediate update whose <see cref="FunctionCallContent"/> holds the
/// accumulated arguments (partial JSON mid-stream is not parsed — empty dictionary), and the
/// last tool-call chunk carries the full JSON once the concatenation becomes parseable.
/// These tests pin the progressive behavior: multiple tool-call updates and full argument
/// accumulation in <see cref="CompletionsResult.AccumulatedToolCalls"/>.
/// </summary>
public class AfToolCallStreamingTests
{
    // ------------------------------------------------------------------
    // (a) Integration: fragmented SSE through AfCompletionsAdapter (own transport)
    // ------------------------------------------------------------------

    // Fragmented tool call: chunk1 = id+name+empty args, chunk2/3 = argument pieces,
    // chunk4 = finish_reason=tool_calls. Fragments concatenate to {"filePath":"a.cs"}.
    private const string FragmentedToolCallSse = """
        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":""}}]},"finish_reason":null}],"usage":null}

        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"file"}}]},"finish_reason":null}],"usage":null}

        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"Path\":\"a.cs\"}"}}]},"finish_reason":null}],"usage":null}

        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{},"finish_reason":"tool_calls"}],"usage":null}

        data: [DONE]

        """;

    /// <summary>
    /// Fragmented SSE through the own transport: intermediate updates surface the tool call
    /// progressively (the first with id + name and no parseable arguments yet, the last with
    /// the full JSON), and <see cref="AfUsageHelpers.AccumulateToolCall"/> ends up with the
    /// fully glued arguments, id and name in place.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task GetCompletionsAsync_FragmentedSseToolCall_ProgressiveUpdatesWithFullAccumulation()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithHeader("Cache-Control", "no-cache")
                .WithBody(FragmentedToolCallSse));

        var adapter = new AfCompletionsAdapter(
            () => new ConnectionProfile { Endpoint = server.Urls[0], ApiKey = "key", Model = "test-model", Stream = true },
            new HttpClient(),
            () => 30);

        var session = new ConversationSession { Id = "s" };
        var capture = new CompletionsResult();

        // The mapper shares the same ToolCall instances across deltas, so the progressive
        // intermediate values must be snapshotted DURING enumeration.
        var argumentSnapshots = new List<string>();
        var toolCallDeltas = 0;
        await foreach (var delta in adapter.GetCompletionsAsync(
            [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], [], session, capture, TestContext.Current.CancellationToken))
        {
            if (delta.ToolCalls is { Count: > 0 } toolCalls)
            {
                toolCallDeltas++;
                argumentSnapshots.Add(toolCalls[0].Function.Arguments);
            }
        }

        // Progressive emission: MORE THAN ONE delta carries the tool call (the SDK bridge
        // yielded exactly one buffered update after finish_reason).
        Assert.True(toolCallDeltas >= 2, $"expected progressive tool-call updates, got {toolCallDeltas}");

        // First update: id + name are in place, arguments not yet parseable (partial JSON
        // mid-stream is not parsed - empty dictionary).
        Assert.Equal(string.Empty, argumentSnapshots[0]);

        // Final accumulation: arguments fully glued, name/id in place.
        var toolCall = capture.AccumulatedToolCalls!.Single();
        Assert.Equal("call_1", toolCall.Id);
        Assert.Equal("read_file", toolCall.Function.Name);
        Assert.Equal("""{"filePath":"a.cs"}""", toolCall.Function.Arguments);
        Assert.Equal(0, toolCall.Index);

        // The last snapshot carries the full JSON.
        Assert.Equal("""{"filePath":"a.cs"}""", argumentSnapshots[^1]);

        // Finish reason survives.
        Assert.Equal("tool_calls", capture.FinishReason);
    }

    // ------------------------------------------------------------------
    // (b) Unit: progressive updates (own transport shape) map to intermediate deltas
    // ------------------------------------------------------------------

    /// <summary>
    /// The own transport emits one update per SSE chunk: the first carries id + name with
    /// arguments not yet parseable, the next carries the accumulated full JSON. The mapper
    /// yields an intermediate delta per update and AccumulateToolCall ends up with the
    /// fully glued arguments.
    /// </summary>
    [Fact]
    public async Task PumpAsync_ProgressiveToolCallUpdates_IntermediateDeltasThenFull()
    {
        var updates = new[]
        {
            // chunk 1: id + name, arguments not yet parseable (empty dictionary)
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m1",
                Contents = [new FunctionCallContent("call_1", "read_file", new Dictionary<string, object?>())]
            },
            // chunk 2: accumulated arguments now parseable (full JSON)
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m1",
                Contents = [new FunctionCallContent("call_1", "read_file", new Dictionary<string, object?> { ["filePath"] = "a.cs" })]
            },
            // chunk 3: finish_reason only
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m1",
                FinishReason = ChatFinishReason.ToolCalls
            }
        };

        var session = new ConversationSession { Id = "s" };
        var capture = new CompletionsResult();

        var argumentSnapshots = new List<string>();
        var deltas = new List<ChatDelta>();
        await foreach (var delta in AfDeltaMapper.PumpAsync(
            ToAsyncEnumerable(updates), session, capture, TestContext.Current.CancellationToken))
        {
            deltas.Add(delta);
            if (delta.ToolCalls is { Count: > 0 } toolCalls)
            {
                argumentSnapshots.Add(toolCalls[0].Function.Arguments);
            }
        }

        // Two deltas carry the tool call (one per tool-call chunk), the finish-reason
        // chunk adds none.
        Assert.Equal(2, deltas.Count(d => d.ToolCalls is not null));
        Assert.Equal(string.Empty, argumentSnapshots[0]);
        Assert.Equal("""{"filePath":"a.cs"}""", argumentSnapshots[^1]);

        var toolCall = capture.AccumulatedToolCalls!.Single();
        Assert.Equal("call_1", toolCall.Id);
        Assert.Equal("read_file", toolCall.Function.Name);
        Assert.Equal("""{"filePath":"a.cs"}""", toolCall.Function.Arguments);

        Assert.Equal("tool_calls", capture.FinishReason);
    }

    /// <summary>
    /// The progressive stream re-delivers the FULL accumulated arguments on every chunk;
    /// the token estimate must count only the per-chunk increment (last-wins growth per
    /// call id), not the whole payload again — summing every accumulated snapshot would
    /// over-count quadratically.
    /// </summary>
    [Fact]
    public async Task PumpAsync_ProgressiveAccumulatedArgs_TokenEstimateLinearNotQuadratic()
    {
        var session = new ConversationSession { Id = "s" };
        var capture = new CompletionsResult();

        // Chunk i re-delivers the accumulated arguments grown by ~100 chars (~100*i total).
        var updates = Enumerable.Range(1, 5).Select(i => new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
        {
            MessageId = "m1",
            Contents = [new FunctionCallContent("call_1", "read_file", new Dictionary<string, object?>
            {
                ["filePath"] = "a.cs",
                ["data"] = new string('x', 100 * i)
            })]
        });

        await foreach (var _ in AfDeltaMapper.PumpAsync(
            ToAsyncEnumerable(updates), session, capture, TestContext.Current.CancellationToken))
        {
        }

        // Linear: ~final accumulated length / 4 (~130 tokens for ~530 chars). The quadratic
        // sum of all accumulated payloads (~1650 chars) would yield ~400 tokens.
        Assert.InRange(capture.CompletionTokens, 100, 200);
        Assert.Equal(capture.CompletionTokens, session.TotalTokens);
    }

    /// <summary>
    /// A single complete <see cref="FunctionCallContent"/> (non-fragmented tool call or
    /// non-streaming response) maps to one delta carrying the full tool call, accumulated
    /// into the capture.
    /// </summary>
    [Fact]
    public async Task PumpAsync_SingleCompleteToolCallUpdate_OneDeltaWithFullToolCall()
    {
        var updates = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m1",
                Contents = [new FunctionCallContent("call_1", "read_file", new Dictionary<string, object?> { ["filePath"] = "a.cs" })]
            }
        };

        var session = new ConversationSession { Id = "s" };
        var capture = new CompletionsResult();

        var deltas = await Collect(AfDeltaMapper.PumpAsync(
            ToAsyncEnumerable(updates), session, capture, TestContext.Current.CancellationToken));

        // One renderable delta carrying the complete tool call.
        var toolCallDeltas = deltas.Where(d => d.ToolCalls is not null).ToList();
        Assert.Single(toolCallDeltas);
        var delta = toolCallDeltas[0];
        Assert.Null(delta.Content);

        var toolCall = capture.AccumulatedToolCalls!.Single();
        Assert.Equal("call_1", toolCall.Id);
        Assert.Equal("read_file", toolCall.Function.Name);
        Assert.Equal("""{"filePath":"a.cs"}""", toolCall.Function.Arguments);
    }

    /// <summary>
    /// Guard documentation: a FunctionCallContent with an empty CallId is IGNORED by
    /// AccumulateToolCall (returns the list unchanged). An empty NAME is no longer a
    /// drop reason: continuation pieces may carry no name (the name arrived with the
    /// first piece or lands in a later one) — dropping them would truncate the
    /// accumulated arguments (the observed intermittent 'arguments lost' bug).
    /// </summary>
    [Fact]
    public void AccumulateToolCall_EmptyCallIdOrName_Ignored()
    {
        var turnAcc = new List<ToolCall>();

        var noId = new FunctionCallContent("", "read_file", new Dictionary<string, object?> { ["filePath"] = "a.cs" });
        Assert.Same(turnAcc, AfUsageHelpers.AccumulateToolCall(noId, turnAcc));
        Assert.Empty(turnAcc);

        // A fragment with neither id nor name (pure argument piece) is ignored too.
        var fragment = new FunctionCallContent("", "", null);
        Assert.Same(turnAcc, AfUsageHelpers.AccumulateToolCall(fragment, turnAcc));
        Assert.Empty(turnAcc);
    }

    [Fact]
    public void AccumulateToolCall_ContinuationWithoutName_AppendsArguments()
    {
        // First piece carries the name, continuation pieces do not (standard OpenAI
        // fragmentation): the accumulated arguments must not be truncated.
        var turnAcc = new List<ToolCall>();
        var first = new FunctionCallContent("call_1", "delegate_task", new Dictionary<string, object?> { ["tasks"] = "partial" });
        AfUsageHelpers.AccumulateToolCall(first, turnAcc);

        var continuation = new FunctionCallContent("call_1", "", new Dictionary<string, object?> { ["tasks"] = "full" });
        AfUsageHelpers.AccumulateToolCall(continuation, turnAcc);

        var toolCall = Assert.Single(turnAcc);
        Assert.Equal("delegate_task", toolCall.Function.Name);
        Assert.Contains("full", toolCall.Function.Arguments);
    }

    [Fact]
    public void AccumulateToolCall_NameLandsInLaterPiece_FillsName()
    {
        var turnAcc = new List<ToolCall>();
        var first = new FunctionCallContent("call_1", "", new Dictionary<string, object?> { ["a"] = 1 });
        AfUsageHelpers.AccumulateToolCall(first, turnAcc);

        var second = new FunctionCallContent("call_1", "read_file", new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 });
        AfUsageHelpers.AccumulateToolCall(second, turnAcc);

        var toolCall = Assert.Single(turnAcc);
        Assert.Equal("read_file", toolCall.Function.Name);
        Assert.Contains("b", toolCall.Function.Arguments);
    }

    [Fact]
    public void AccumulateToolCall_EmptyArguments_KeepsPreviousPartial()
    {
        // An unparseable piece yields an EMPTY parsed dictionary — it must not erase
        // the previously accumulated arguments.
        var turnAcc = new List<ToolCall>();
        var first = new FunctionCallContent("call_1", "read_file", new Dictionary<string, object?> { ["filePath"] = "a.cs" });
        AfUsageHelpers.AccumulateToolCall(first, turnAcc);

        var empty = new FunctionCallContent("call_1", "", new Dictionary<string, object?>());
        AfUsageHelpers.AccumulateToolCall(empty, turnAcc);

        var toolCall = Assert.Single(turnAcc);
        Assert.Contains("a.cs", toolCall.Function.Arguments);
    }

    // ------------------------------------------------------------------
    // (b2) Unit: per-turn token counters reset on the turn boundary — the next turn's
    //      badge must not duplicate the previous message (or freeze on it when Usage
    //      arrived in turn 1 and disabled the heuristic)
    // ------------------------------------------------------------------

    /// <summary>
    /// Two turns (distinct MessageId): the turn-2 estimate must NOT include turn-1 tokens.
    /// Without the reset the estimate kept adding to the run-wide counter — the turn-2
    /// badge duplicated the previous message's count.
    /// </summary>
    [Fact]
    public async Task PumpAsync_SecondTurn_TokenEstimateRebasedFromSessionTotal()
    {
        var session = new ConversationSession { Id = "s" };
        var capture = new CompletionsResult();

        var updates = new[]
        {
            // Turn 1: ~400 chars → ~100 tokens estimated
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m1",
                Contents = [new TextContent(new string('a', 400))]
            },
            // Turn boundary
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m2"
            },
            // Turn 2: ~40 chars → ~10 tokens estimated
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m2",
                Contents = [new TextContent(new string('b', 40))]
            }
        };

        await foreach (var _ in AfDeltaMapper.PumpAsync(
            ToAsyncEnumerable(updates), session, capture, TestContext.Current.CancellationToken))
        {
        }

        // Turn 2 estimate counts ONLY turn-2 chars (~10 tokens), not turn-1 (~100).
        // The heuristic adds Math.Max(1, chars/4) per delta.
        Assert.InRange(capture.CompletionTokens, 1, 30);

        // The session total keeps the FULL context position (turn-1 visible + turn-2).
        Assert.Equal(110, session.TotalTokens);
    }

    /// <summary>
    /// Usage arrived in turn 1 (heuristic disabled for the rest of the run): without the
    /// reset the turn-2 badge froze on the turn-1 count. With the reset the turn-2
    /// estimate works again and the final usage chunk overwrites capture.Usage.
    /// </summary>
    [Fact]
    public async Task PumpAsync_UsageInFirstTurn_SecondTurnEstimateStillWorks()
    {
        var session = new ConversationSession { Id = "s" };
        var capture = new CompletionsResult();

        var updates = new[]
        {
            // Turn 1: text + usage (heuristic disabled from here on)
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m1",
                Contents = [new TextContent(new string('a', 400)), new UsageContent(new UsageDetails
                {
                    InputTokenCount = 10,
                    OutputTokenCount = 100,
                    TotalTokenCount = 110
                })]
            },
            // Turn boundary
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m2"
            },
            // Turn 2: text only — the estimate must count it (heuristic re-enabled)
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m2",
                Contents = [new TextContent(new string('b', 40))]
            }
        };

        await foreach (var _ in AfDeltaMapper.PumpAsync(
            ToAsyncEnumerable(updates), session, capture, TestContext.Current.CancellationToken))
        {
        }

        // Turn 2 estimate: ~10 tokens (40 chars / 4), NOT frozen at the turn-1 usage value.
        Assert.InRange(capture.CompletionTokens, 1, 30);

        // Session total rebased from the post-usage context position (110 - reasoning 0)
        // + turn-2 visible tokens (~10).
        Assert.Equal(120, session.TotalTokens);
    }

    // ------------------------------------------------------------------
    // (c) Unit: two tool calls in one update -> order preserved
    // ------------------------------------------------------------------

    [Fact]
    public async Task PumpAsync_TwoToolCallsInOneUpdate_OrderPreserved()
    {
        var updates = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m1",
                Contents =
                [
                    new FunctionCallContent("call_1", "read_file", new Dictionary<string, object?> { ["filePath"] = "a.cs" }),
                    new FunctionCallContent("call_2", "bash", new Dictionary<string, object?> { ["command"] = "ls" })
                ]
            }
        };

        var session = new ConversationSession { Id = "s" };
        var capture = new CompletionsResult();

        var deltas = await Collect(AfDeltaMapper.PumpAsync(
            ToAsyncEnumerable(updates), session, capture, TestContext.Current.CancellationToken));

        // Both tool calls in one delta, in wire order.
        var delta = deltas.Single(d => d.ToolCalls is not null);
        Assert.Equal(2, delta.ToolCalls!.Count);
        Assert.Equal("call_1", delta.ToolCalls[0].Id);
        Assert.Equal("read_file", delta.ToolCalls[0].Function.Name);
        Assert.Equal("call_2", delta.ToolCalls[1].Id);
        Assert.Equal("bash", delta.ToolCalls[1].Function.Name);

        // Same order in the capture.
        Assert.Equal(2, capture.AccumulatedToolCalls!.Count);
        Assert.Equal("call_1", capture.AccumulatedToolCalls[0].Id);
        Assert.Equal("call_2", capture.AccumulatedToolCalls[1].Id);
        Assert.Equal(0, capture.AccumulatedToolCalls[0].Index);
        Assert.Equal(1, capture.AccumulatedToolCalls[1].Index);
    }

    // ------------------------------------------------------------------
    // (d) Unit: tool call + text in one update -> both in the delta
    // ------------------------------------------------------------------

    [Fact]
    public async Task PumpAsync_ToolCallAndTextInOneUpdate_BothInDelta()
    {
        var updates = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m1",
                Contents =
                [
                    new TextContent("Let me read that file."),
                    new FunctionCallContent("call_1", "read_file", new Dictionary<string, object?> { ["filePath"] = "a.cs" })
                ]
            }
        };

        var session = new ConversationSession { Id = "s" };
        var capture = new CompletionsResult();

        var deltas = await Collect(AfDeltaMapper.PumpAsync(
            ToAsyncEnumerable(updates), session, capture, TestContext.Current.CancellationToken));

        // The delta carries BOTH the text and the tool calls.
        var delta = deltas.Single(d => d.ToolCalls is not null);
        Assert.Equal("Let me read that file.", delta.Content);
        Assert.Single(delta.ToolCalls!);
        Assert.Equal("call_1", delta.ToolCalls![0].Id);

        Assert.Single(capture.AccumulatedToolCalls!);
        Assert.Equal("call_1", capture.AccumulatedToolCalls![0].Id);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToAsyncEnumerable(IEnumerable<ChatResponseUpdate> updates)
    {
        foreach (var update in updates)
        {
            await Task.Yield();
            yield return update;
        }
    }

    private static async Task<List<ChatDelta>> Collect(IAsyncEnumerable<ChatDelta> stream)
    {
        var list = new List<ChatDelta>();
        await foreach (var delta in stream)
        {
            list.Add(delta);
        }

        return list;
    }
}
