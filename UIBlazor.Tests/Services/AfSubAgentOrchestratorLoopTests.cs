using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace UIBlazor.Tests.Services;

/// <summary>
/// The branch TURN LOOP of <see cref="AfSubAgentOrchestrator"/>: one LLM call per turn and the
/// branch history is rebuilt from the branch SESSION before every turn, so pause, compression,
/// interjects and the token budget all apply BETWEEN turns.
/// <para>
/// The real <see cref="ChatService"/> runs here (see <see cref="WireScriptedChatService"/>): only
/// the transport is scripted, at the wire level, and the updates go through the real
/// <see cref="AfDeltaMapper"/>. Token accounting, tool-call accumulation, delta application and
/// the message close all run in shipping code — a hand-written stub would have tested a copy of
/// them instead.
/// </para>
/// <para>
/// Scripted turns are indexed by LLM TURN, not by HTTP request: a compression request goes
/// through its own gateway and does not consume a scripted turn.
/// </para>
/// </summary>
public class AfSubAgentOrchestratorLoopTests
{
    private const string Args = """{"tasks":[{"task":"do it","systemPrompt":"sp","allowedTools":[]}]}""";

    /// <summary>Text of a tool turn <paramref name="index"/> (each turn has its own, so a
    /// collapsed turn is individually identifiable in the wire history).</summary>
    private static string TurnText(int index) => $"Let me read file {index} first, then continue.";

    /// <summary>Content of the stub result the orchestrator gives an unexecuted call.</summary>
    private const string InterruptedMarker = "not executed";

    private readonly Mock<IProfileManager> _profileManager = new();
    private readonly Mock<ISystemPromptBuilder> _promptBuilder = new();
    private readonly Mock<IToolManager> _toolManager = new();
    private readonly Mock<IContentFilter> _contentFilter = new();
    private readonly Mock<ICommonSettingsProvider> _commonSettings = new();
    private readonly Mock<IRetryHandler> _retryHandler = new();

    /// <summary>Number of real tool executions performed by the branch.</summary>
    private int _executedToolCalls;

    public AfSubAgentOrchestratorLoopTests()
    {
        _promptBuilder
            .Setup(b => b.PrepareSubAgentSystemPromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sub prompt");

        // A REGISTERED tool: the branch gets it through BuildSubAgentTools (the whitelist is null
        // for an empty allowedTools array). Counting the executions here is what makes "while
        // paused, no tool ran" a real assertion instead of a dead counter.
        var tools = new[]
        {
            CreateCountingTool(BuiltInToolEnum.ReadFiles),
            CreateCountingTool(BuiltInToolEnum.SearchFiles)
        };
        _toolManager
            .Setup(t => t.GetEnabledTools(AppMode.Agent))
            .Returns(tools);

        // The branch handler (ToolCallHandler) resolves a call by NAME and asks for its approval
        // mode; without these two the call degrades to "unknown tool" and never executes.
        foreach (var tool in tools)
        {
            _toolManager.Setup(t => t.GetTool(tool.Name)).Returns(tool);
            _toolManager
                .Setup(t => t.GetApprovalModeByToolName(tool.Name))
                .Returns(ToolApprovalMode.Allow);
        }

        // MaxRetries = 1: no retries, a failure rethrows immediately (keeps turn counting exact).
        _commonSettings.SetupGet(s => s.Current).Returns(new CommonOptions { MaxRetries = 1 });
        _retryHandler
            .Setup(r => r.WaitForRetryAsync(It.IsAny<int>(), It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private Tool CreateCountingTool(string name, Action? onExecute = null) => new()
    {
        Name = name,
        DisplayName = name,
        NativeTool = new NativeToolDefinition
        {
            Function = new NativeToolFunction
            {
                Name = name,
                Description = $"Test tool {name}",
                Parameters = new NativeParameters { Type = NativeToolType.Object, Properties = [] }
            }
        },
        ExecuteAsync = (_, _) =>
        {
            Interlocked.Increment(ref _executedToolCalls);
            onExecute?.Invoke();
            return Task.FromResult(new VsToolResult { Name = name, Result = $"ran {name}" });
        }
    };

    /// <summary>Number of tool executions observed so far.</summary>
    private int ExecutedToolCalls => Volatile.Read(ref _executedToolCalls);

    // ------------------------------------------------------------------------------------
    // Iteration cap
    // ------------------------------------------------------------------------------------

    [Fact(Timeout = 120000)]
    public async Task Branch_MaxIterationsUnlimited_DoesNotStopAt40()
    {
        // Regression for the retired AfAgentLoop default (`maxIterations ?? 40`):
        // MaxIterationsPerSubAgent = 0 means UNLIMITED, not "the old default of 40".
        using var chat = CreateChat((i, ct) => ToolCallsThenTextScript(i, toolTurns: 45, null, ct));
        var orchestrator = CreateOrchestrator(chat.Service, profile => profile.MaxIterationsPerSubAgent = 0);
        var toolCall = CreateDelegateTaskCall();

        var result = await orchestrator.ExecuteAsync(Args, toolCall, TestContext.Current.CancellationToken);

        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Completed, branch.Status);
        Assert.Contains("Hello world!", result.Result);

        // 45 tool turns + the closing text turn: the loop never stopped at 40.
        Assert.Equal(46, chat.Turns);
        Assert.DoesNotContain("Iteration limit reached", result.Result);
    }

    [Fact(Timeout = 60000)]
    public async Task Branch_IterationCapReached_ExecutesCallsOfLastTurn()
    {
        // A capped run still EXECUTES the tool calls of the cap turn (their results belong into
        // the history) and reports the cap to the parent agent.
        using var chat = CreateChat(AlwaysToolCallScript);
        var orchestrator = CreateOrchestrator(chat.Service, profile => profile.MaxIterationsPerSubAgent = 3);
        var toolCall = CreateDelegateTaskCall();

        var result = await orchestrator.ExecuteAsync(Args, toolCall, TestContext.Current.CancellationToken);

        Assert.Equal(3, chat.Turns);
        Assert.Equal(3, ExecutedToolCalls);

        var branch = Assert.Single(toolCall.SubAgents!);
        var calls = branch.GetMessages().SelectMany(m => m.ToolCalls ?? []).ToList();
        Assert.Equal(3, calls.Count);
        Assert.All(calls, call => Assert.NotNull(call.Result));

        Assert.Contains("Iteration limit reached", result.Result);
    }

    // ------------------------------------------------------------------------------------
    // Pause gate
    // ------------------------------------------------------------------------------------

    [Fact(Timeout = 30000)]
    public async Task Branch_PauseGate_BlocksBeforeNextTurnAndToolExecution()
    {
        var toolCall = CreateDelegateTaskCall();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var chat = CreateChat((i, ct) => ToolCallsThenTextScript(i, toolTurns: 1, () =>
        {
            // Pause WHILE turn 1 streams: the turn finishes, but neither the tool batch of that
            // turn nor the next LLM call may start until the user resumes the branch.
            toolCall.SubAgents?[0].Pause();
            paused.TrySetResult();
        }, ct));

        var orchestrator = CreateOrchestrator(chat.Service);
        var run = orchestrator.ExecuteAsync(Args, toolCall, TestContext.Current.CancellationToken);

        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var subAgent = toolCall.SubAgents![0];
        Assert.True(subAgent.IsPaused);

        // While paused: no further LLM call and no tool execution.
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(1, chat.Turns);
        Assert.Equal(0, ExecutedToolCalls);

        // Resume: the tool batch executes and turn 2 is called.
        subAgent.Resume();
        var result = await run;

        Assert.Equal(2, chat.Turns);
        Assert.Equal(1, ExecutedToolCalls);
        Assert.Equal(SubAgentStatus.Completed, subAgent.Status);
        Assert.Contains("Hello world!", result.Result);
    }

    // ------------------------------------------------------------------------------------
    // Compression between turns
    // ------------------------------------------------------------------------------------

    [Fact(Timeout = 60000)]
    public async Task Branch_CompressionBetweenTurns_BranchSessionCompacted()
    {
        // The gate is checked before EVERY turn, but the real planner only collapses when the
        // history has a head, a middle and a tail to keep (>= 8 visible messages). A two-turn
        // branch therefore fires the gate and legitimately declines — the run is scripted long
        // enough for the compression to actually happen.
        using var chat = CreateChat((i, ct) => ToolCallsThenTextScript(i, toolTurns: 8, null, ct));
        var orchestrator = CreateOrchestrator(chat.Service, profile => profile.TokensToCompress = 1);

        var result = await orchestrator.ExecuteAsync(
            Args, CreateDelegateTaskCall(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);

        // The gate fired and a summary was produced at least once.
        Assert.True(
            chat.CompressionCalls >= 1,
            $"the branch gate must compress a long branch history (calls: {chat.CompressionCalls})");

        // The summary reached the model: some turn after the first one was built from the
        // COMPACTED session, so the pre-compression turn texts are gone from that request.
        var compactedTurn = Enumerable.Range(1, chat.Turns - 1)
            .FirstOrDefault(index => chat.Wire(index)
                .Any(m => m.Content == WireScriptedChatService.SummaryText));

        Assert.True(
            compactedTurn > 0,
            "the compacted branch session must travel to the model on a later turn");

        // Structural proof that the history SHRANK: the request carrying the summary is shorter
        // than the one before it, even though one more turn was appended in between. Asserting on
        // a specific turn text would be wrong here - the planner deliberately keeps a verbatim
        // tail, so the newest turns (and the head) survive by design.
        Assert.True(
            chat.Wire(compactedTurn).Count < chat.Wire(compactedTurn - 1).Count,
            $"compression must shorten the request (turn {compactedTurn - 1}: " +
            $"{chat.Wire(compactedTurn - 1).Count} messages, turn {compactedTurn}: " +
            $"{chat.Wire(compactedTurn).Count} messages)");
    }

    // ------------------------------------------------------------------------------------
    // Interject between turns
    // ------------------------------------------------------------------------------------

    [Fact(Timeout = 30000)]
    public async Task Branch_InterjectBetweenTurns_ReachesBranchModel()
    {
        var toolCall = CreateDelegateTaskCall();
        var interjectAccepted = false;

        using var chat = CreateChat((i, ct) => InterjectAfterTextTurnScript(i, () => { }, () => interjectAccepted = toolCall.SubAgents![0].TryAddInterject("also check the tests"), ct));
        var orchestrator = CreateOrchestrator(chat.Service);

        var result = await orchestrator.ExecuteAsync(Args, toolCall, TestContext.Current.CancellationToken);

        Assert.True(interjectAccepted, "the interject must be accepted by a running branch");
        Assert.Equal(2, chat.Turns);

        // Turn 2 carries the interject as a user message in the rebuilt wire history.
        var turn2Wire = chat.Wire(1);
        Assert.Contains(turn2Wire, m => m.Role == ChatMessageRole.User && m.Content == "also check the tests");
        Assert.Contains("Hello world!", result.Result);
    }

    // ------------------------------------------------------------------------------------
    // Token budget across turns
    // ------------------------------------------------------------------------------------

    [Fact(Timeout = 30000)]
    public async Task Branch_TokenBudget_AccumulatesAcrossTurns()
    {
        using var chat = CreateChat((i, ct) => ToolCallsThenTextScript(i, toolTurns: 2, null, ct));
        var orchestrator = CreateOrchestrator(chat.Service, profile => profile.MaxTokensPerSubAgent = 20);
        var toolCall = CreateDelegateTaskCall();

        var result = await orchestrator.ExecuteAsync(Args, toolCall, TestContext.Current.CancellationToken);

        Assert.Equal(3, chat.Turns);

        // Usage is the CONTEXT size of each request (15, 30, 45 — the prompt grows every turn),
        // so the budget is read from the SESSION total and does not restart per turn.
        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(45, branch.TotalTokens);
        Assert.Contains("Token budget exceeded", result.Result);
    }

    // ------------------------------------------------------------------------------------
    // Cancellation
    // ------------------------------------------------------------------------------------

    [Fact(Timeout = 30000)]
    public async Task Branch_CancelledMidTurn_StatusCancelled_NoFurtherCalls()
    {
        using var cts = new CancellationTokenSource();

        // Cancel WHILE turn 1 streams: the branch must stop as Cancelled and must not fire
        // another LLM call.
        using var chat = CreateChat((_, ct) => CancelDuringTextTurnScript(cts, ct));

        var orchestrator = CreateOrchestrator(chat.Service);
        var toolCall = CreateDelegateTaskCall();

        var result = await orchestrator.ExecuteAsync(Args, toolCall, cts.Token);

        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Cancelled, branch.Status);
        Assert.False(result.Success);
        Assert.Equal(1, chat.Turns);

        // No frozen bubble is left behind: every message of the branch is closed.
        Assert.All(branch.GetMessages(), m => Assert.False(m.IsStreaming));
    }

    [Fact(Timeout = 30000)]
    public async Task Branch_ToolBatchInterrupted_AllCallsHaveResult()
    {
        using var cts = new CancellationTokenSource();

        // Cancel from INSIDE the batch (the first tool cancels the run): the batch is
        // interrupted halfway, and every call still needs a Result — an assistant(tool_calls)
        // turn without matching tool messages is a hard API 400 on the next request.
        //
        // Cancelling instead DURING the stream cannot be used to reproduce this: the token is
        // checked by WithCancellation between updates, so the turn itself throws and the batch
        // is never entered (that scenario is Branch_CancelledMidTurn_StatusCancelled_NoFurtherCalls).
        var cancelling = CreateCountingTool(BuiltInToolEnum.ReadFiles, onExecute: () => cts.Cancel());
        _toolManager.Setup(t => t.GetTool(BuiltInToolEnum.ReadFiles)).Returns(cancelling);

        using var chat = CreateChat(TwoCallsThenStop);
        var orchestrator = CreateOrchestrator(chat.Service);
        var toolCall = CreateDelegateTaskCall();

        var result = await orchestrator.ExecuteAsync(Args, toolCall, cts.Token);

        var branch = Assert.Single(toolCall.SubAgents!);
        var calls = branch.GetMessages().SelectMany(m => m.ToolCalls ?? []).ToList();
        Assert.Equal(2, calls.Count);

        // The invariant that matters: NO call is left without an answer.
        Assert.All(calls, call => Assert.NotNull(call.Result));

        // The call that ran has its real result; the one the interruption skipped is stubbed.
        Assert.True(calls[0].Result!.Success);
        Assert.False(calls[1].Result!.Success);
        Assert.Contains(InterruptedMarker, calls[1].Result!.Content);

        // The root cancellation unwinds the batch: Cancelled status, no further LLM turn.
        Assert.Equal(SubAgentStatus.Cancelled, branch.Status);
        Assert.False(result.Success);
        Assert.Equal(1, chat.Turns);
    }

    // ------------------------------------------------------------------------------------
    // Wire scripts
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// A <see cref="WireTurn"/> is an ASYNC ITERATOR, and a lambda cannot be one — the scripts
    /// below are therefore methods. The service consumes them lazily, so every statement between
    /// two <c>yield return</c>s runs at exactly that point of the turn (that is how pause,
    /// interject and cancel are injected mid-turn).
    /// </summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> ToolCallsThenTextScript(
        int index, int toolTurns, Action? onFirstToolTurnStreamed, CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (index < toolTurns)
        {
            yield return Upd.Text(TurnText(index));

            if (index == 0)
            {
                onFirstToolTurnStreamed?.Invoke();
            }

            yield return Upd.Call($"call_{index + 1}", BuiltInToolEnum.ReadFiles);
            yield return Upd.Usage(15 * (index + 1));
            yield return Upd.Stop();
            yield break;
        }

        yield return Upd.Text("Hello world!");
        yield return Upd.Usage(15 * (index + 1));
        yield return Upd.Stop();
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Every turn produces one tool call and never a closing text turn.</summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> AlwaysToolCallScript(
        int index, CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return Upd.Text(TurnText(index));
        yield return Upd.Call($"call_{index + 1}", BuiltInToolEnum.ReadFiles);
        yield return Upd.Usage(15 * (index + 1));
        yield return Upd.Stop();
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Turn 1 is plain text; an interject added while it streams forces turn 2.</summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> InterjectAfterTextTurnScript(
        int index, Action onBeforeInterject, Func<bool> addInterject, CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return Upd.Text("Hello world!");

        if (index == 0)
        {
            // Without the interject the run would END here (a text turn without tool calls).
            onBeforeInterject();
            addInterject();
        }

        yield return Upd.Usage(15 * (index + 1));
        yield return Upd.Stop();
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Cancels the run after the first update of turn 1.</summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> CancelDuringTextTurnScript(
        CancellationTokenSource cts, CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return Upd.Text("Hello world!");
        await cts.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Turn 1 produces two calls and completes; the batch is interrupted later.</summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> TwoCallsThenStop(
        int index, CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return Upd.Text(TurnText(index));
        yield return Upd.Call("call_1", BuiltInToolEnum.ReadFiles);
        yield return Upd.Call("call_2", BuiltInToolEnum.SearchFiles);
        yield return Upd.Usage(15);
        yield return Upd.Stop();
        cancellationToken.ThrowIfCancellationRequested();
    }

    // ------------------------------------------------------------------------------------
    // Infrastructure
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the real service. The token numbers live in the scripts themselves (each turn
    /// reports its own context size through <see cref="Upd.Usage"/>).
    /// </summary>
    private WireScriptedChatService CreateChat(WireTurn turn)
        => WireScriptedChatService.Create(
            turn,
            new ConnectionProfile
            {
                Endpoint = "http://localhost:1",
                ApiKey = "key",
                Model = "test-model"
            },
            tools: [CreateCountingTool(BuiltInToolEnum.ReadFiles), CreateCountingTool(BuiltInToolEnum.SearchFiles)]);

    private AfSubAgentOrchestrator CreateOrchestrator(
        IChatService chatService, Action<ConnectionProfile>? configure = null)
    {
        var profile = new ConnectionProfile
        {
            Endpoint = "http://localhost:1",
            ApiKey = "key",
            Model = "test-model"
        };
        configure?.Invoke(profile);
        _profileManager.SetupGet(p => p.ActiveProfile).Returns(profile);

        return new AfSubAgentOrchestrator(
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            chatService,
            _contentFilter.Object,
            new HttpClient(),
            _commonSettings.Object,
            _retryHandler.Object,
            NullLogger<AfSubAgentOrchestrator>.Instance);
    }

    private static ToolCall CreateDelegateTaskCall() => new()
    {
        Id = "tc-1",
        Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
    };
}
