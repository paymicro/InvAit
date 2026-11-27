using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Regression: branch text produced by AfSubAgentOrchestrator.RunBranchAsync must reach
/// the delegate_task VsToolResult (ticket.ResultText) - the branch return value was discarded
/// before, so the model received "(Sub-agent returned an empty response.)" instead of the
/// branch answer. The batch then lost it a SECOND time, in ToolResult.Convert, whenever any
/// branch had failed.
/// </summary>
public class AfSubAgentOrchestratorResultTests
{
    private readonly Mock<IProfileManager> _profileManager = new();
    private readonly Mock<ISystemPromptBuilder> _promptBuilder = new();
    private readonly Mock<IToolManager> _toolManager = new();
    private readonly Mock<IContentFilter> _contentFilter = new();
    private readonly Mock<ICommonSettingsProvider> _commonSettings = new();
    private readonly Mock<IRetryHandler> _retryHandler = new();
    private readonly WireMockServer _server = WireMockServer.Start();

    public AfSubAgentOrchestratorResultTests()
    {
        _server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("no model"));

        _profileManager.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = _server.Urls[0],
            ApiKey = "key",
            Model = "test-model"
        });
        _promptBuilder
            .Setup(b => b.PrepareSubAgentSystemPromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sub prompt");
        _toolManager.Setup(t => t.GetEnabledTools(AppMode.Agent)).Returns([]);

        // MaxRetries = 1: no retries, the first failure rethrows (previous behavior).
        _commonSettings.SetupGet(s => s.Current).Returns(new CommonOptions { MaxRetries = 1 });
        _retryHandler
            .Setup(r => r.WaitForRetryAsync(It.IsAny<int>(), It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// The branch fails on the TRANSPORT (404 from the endpoint), exactly like production.
    /// Neither gateway is substituted here: the REAL <see cref="ChatService"/> runs its own
    /// <see cref="AfCompletionsAdapter"/> against the mock server, so the failure surfaces where
    /// the orchestrator expects it - inside <c>ProcessStreamAsync</c>, from shipping code.
    /// </summary>
    private static AfSubAgentOrchestrator CreateOrchestrator(
        ScriptedChatService chatService,
        IProfileManager profileManager,
        ISystemPromptBuilder promptBuilder,
        IToolManager toolManager,
        IContentFilter contentFilter,
        ICommonSettingsProvider commonSettings,
        IRetryHandler retryHandler)
        => CreateOrchestrator(chatService.Service, profileManager, promptBuilder,
            toolManager, contentFilter, commonSettings, retryHandler);

    private static AfSubAgentOrchestrator CreateOrchestrator(
        IChatService chatService,
        IProfileManager profileManager,
        ISystemPromptBuilder promptBuilder,
        IToolManager toolManager,
        IContentFilter contentFilter,
        ICommonSettingsProvider commonSettings,
        IRetryHandler retryHandler)
        => new(
            profileManager,
            promptBuilder,
            toolManager,
            chatService,
            contentFilter,
            new HttpClient(),
            commonSettings,
            retryHandler,
            NullLogger<AfSubAgentOrchestrator>.Instance);

    [Fact]
    public async Task ExecuteAsync_BranchFails_ResultTextReachesToolResult()
    {
        using var chatService = ScriptedChatService.CreateWithRealAdapter(_profileManager.Object.ActiveProfile);
        var orchestrator = CreateOrchestrator(
            chatService,
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            _contentFilter.Object,
            _commonSettings.Object,
            _retryHandler.Object);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        var result = await orchestrator.ExecuteAsync(
            """{"tasks":[{"task":"do it","systemPrompt":"sp","allowedTools":[]}]}""",
            toolCall,
            TestContext.Current.CancellationToken);

        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Failed, branch.Status);
        Assert.NotEmpty(branch.ErrorMessage!);

        // The failing branch's text is part of the aggregated tool result
        // (it was an empty string before the ResultText fix).
        Assert.Contains("(Sub-agent branch failed", result.Result);
        Assert.False(result.Success);
        Assert.Equal("1 of 1 delegated task(s) failed (Task 1).", result.ErrorMessage);
        // A single task gets no summary line: its only block already is the failed one.
        Assert.DoesNotContain("delegated task(s) failed", result.Result);
        // The reason is what the model reads — ToolResult.Convert used to replace the whole
        // payload with ErrorMessage on failure.
        Assert.Contains("(Sub-agent branch failed",
            ToolResult.Convert(result, string.Empty, BuiltInToolEnum.DelegateTask).Content);
    }

    /// <summary>
    /// The batch the bug was about: one branch produces an answer, another fails. The delegating
    /// call runs its branches concurrently, so their start order is not fixed - the branch's OWN
    /// task text in the wire history is what makes the mix deterministic.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OneBranchFails_KeepsTheOtherAnswerAndNamesTheFailedTask()
    {
        using var chatService = ScriptedChatService.CreateWithRealAdapter(_profileManager.Object.ActiveProfile);
        chatService.Service.ChatGateway = new PerTaskGateway();
        var orchestrator = CreateOrchestrator(
            chatService,
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            _contentFilter.Object,
            _commonSettings.Object,
            _retryHandler.Object);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        var result = await orchestrator.ExecuteAsync(
            """{"tasks":[{"task":"do the good thing"},{"task":"explode on purpose"}]}""",
            toolCall,
            TestContext.Current.CancellationToken);

        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgents![0].Status);
        Assert.Equal(SubAgentStatus.Failed, toolCall.SubAgents![1].Status);
        Assert.False(result.Success);

        // ONE text carries both outcomes: the successful branch's answer AND which task failed.
        Assert.Contains("answer from the healthy branch", result.Result);
        Assert.Contains("(Sub-agent branch failed: provider rejected the branch)", result.Result);
        // The failure summary is a TRAILER: ToolResult.TruncateContent keeps the tail of an
        // oversized result, so a head line would be the first thing a big batch loses.
        Assert.EndsWith("1 of 2 delegated task(s) failed (Task 2).", result.Result.TrimEnd());
        Assert.Equal("1 of 2 delegated task(s) failed (Task 2).", result.ErrorMessage);

        // The regression: this content used to be the single sentence "One or more delegated
        // task(s) failed." because Convert preferred ErrorMessage whenever Success was false.
        var wire = ToolResult.Convert(result, string.Empty, BuiltInToolEnum.DelegateTask);
        Assert.Contains("answer from the healthy branch", wire.Content);
    }

    /// <summary>
    /// The other half of the report: a branch that produced an answer and THEN failed. Its work is
    /// the only part the parent can still use, so it travels with the reason.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_BranchFailsAfterAnswering_KeepsItsLastAnswer()
    {
        // Wire-level script: the real AfDeltaMapper maps the turns, so tool-call accumulation is
        // production code too. One branch = the gateway's turn index is the branch's turn index.
        using var chatService = WireScriptedChatService.Create(
            turn: (index, _) => PartialThenFailTurn(index),
            profile: _profileManager.Object.ActiveProfile);
        var orchestrator = CreateOrchestrator(
            chatService.Service,
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            _contentFilter.Object,
            _commonSettings.Object,
            _retryHandler.Object);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        var result = await orchestrator.ExecuteAsync(
            """{"tasks":[{"task":"do it"}]}""",
            toolCall,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("(Sub-agent branch failed: provider rejected the branch)", result.Result);
        // The answer of turn 1, although the LAST assistant message of the branch (turn 2) is a
        // tool-call turn with no content at all.
        Assert.Contains("Last response: partial answer for the parent", result.Result);
    }

    /// <summary>
    /// Turn 0 answers AND calls a missing tool (so the run continues), turn 1 carries the tool call
    /// only, turn 2 kills the branch.
    /// </summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> PartialThenFailTurn(int turn)
    {
        await Task.Yield();
        switch (turn)
        {
            case 0:
                yield return Upd.Text("partial answer for the parent");
                yield return Upd.Call("tc-1", BuiltInToolEnum.ReadFiles, "{}");
                break;
            case 1:
                yield return Upd.Call("tc-2", BuiltInToolEnum.ReadFiles, "{}");
                break;
            default:
                throw new LlmApiException("provider rejected the branch");
        }
    }

    /// <summary>
    /// Several tasks, none failed: the per-task headers are the only thing connecting an answer to
    /// the task it belongs to, and no summary line may appear.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MultiTask_AllBranchesSucceed_ReportsEachAnswerUnderItsTask()
    {
        using var chatService = ScriptedChatService.Create(Turns.Text("branch answer"));
        var orchestrator = CreateOrchestrator(
            chatService,
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            _contentFilter.Object,
            _commonSettings.Object,
            _retryHandler.Object);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        var result = await orchestrator.ExecuteAsync(
            """{"tasks":[{"task":"do the good thing"},{"task":"and another"}]}""",
            toolCall,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Empty(result.ErrorMessage);
        Assert.Contains("### Task 1: do the good thing", result.Result);
        Assert.Contains("### Task 2: and another", result.Result);
        Assert.DoesNotContain("delegated task(s) failed", result.Result);
    }

    /// <summary>
    /// A UI subscriber that throws while a branch is finalized is a bug in the UI, not a reason to
    /// throw away the delegation: the batch still reports what the branches finished.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_BatchPlumbingThrows_StillReportsWhatFinished()
    {
        using var chatService = ScriptedChatService.CreateWithRealAdapter(_profileManager.Object.ActiveProfile);
        chatService.Service.ChatGateway = new PerTaskGateway();
        var orchestrator = CreateOrchestrator(
            chatService,
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            _contentFilter.Object,
            _commonSettings.Object,
            _retryHandler.Object);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        // Subscribe while the tickets are created (the only moment the sub-agents exist), then blow
        // up on the terminal notification of the FAILED one - inside RunBranchAsync's finally,
        // i.e. outside every per-branch catch.
        toolCall.SubAgentsChanged += () =>
        {
            foreach (var agent in toolCall.SubAgents!)
                agent.StateChanged += () =>
                {
                    if (agent.Status == SubAgentStatus.Failed)
                        throw new InvalidOperationException("ui bug");
                };
        };

        var result = await orchestrator.ExecuteAsync(
            """{"tasks":[{"task":"do the good thing"},{"task":"explode on purpose"}]}""",
            toolCall,
            TestContext.Current.CancellationToken);

        Assert.Contains("answer from the healthy branch", result.Result);
        Assert.Contains("(Sub-agent branch failed: provider rejected the branch)", result.Result);
    }

    /// <summary>
    /// The per-branch TIME LIMIT is a graceful completion, not a failure: the branch reports the
    /// limit as its answer, and since it produces no text of its own the report carries no
    /// "Last response" tail (it used to say "(Sub-agent returned an empty response.)" there).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ExecuteAsync_BranchHitsTimeLimit_ReportsTheLimitWithoutAnEmptyTail()
    {
        _profileManager.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = "http://localhost:1",
            ApiKey = "key",
            Model = "test-model",
            MaxExecutionTimePerSubAgent = 1
        });

        using var chatService = ScriptedChatService.CreateWithRealAdapter(_profileManager.Object.ActiveProfile);
        chatService.Service.ChatGateway = new AnswerOrParkGateway();
        var orchestrator = CreateOrchestrator(
            chatService,
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            _contentFilter.Object,
            _commonSettings.Object,
            _retryHandler.Object);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        var result = await orchestrator.ExecuteAsync(
            """{"tasks":[{"task":"park until cancelled"}]}""",
            toolCall,
            TestContext.Current.CancellationToken);

        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Completed, branch.Status);
        Assert.True(result.Success);
        Assert.StartsWith("Sub-agent exceeded execution time limit", result.Result);
        // A space before the budget note, and no "Last response: (Sub-agent returned an empty
        // response.)" invented for a branch that never spoke.
        Assert.DoesNotContain("Last response:", result.Result);
        Assert.DoesNotContain(")).", result.Result);
    }

    /// <summary>
    /// Cancelling the run must not throw away what already finished: the branch that answered
    /// before the stop is reported, with the notice that the batch was cut short. The other branch
    /// parks inside its stream until the token fires, so the order of the two outcomes is fixed.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ExecuteAsync_CancelledAfterOneBranchAnswered_ReportsThatAnswer()
    {
        using var cts = new CancellationTokenSource();
        using var chatService = ScriptedChatService.CreateWithRealAdapter(_profileManager.Object.ActiveProfile);
        chatService.Service.ChatGateway = new AnswerOrParkGateway();
        var orchestrator = CreateOrchestrator(
            chatService,
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            _contentFilter.Object,
            _commonSettings.Object,
            _retryHandler.Object);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        var run = orchestrator.ExecuteAsync(
            """{"tasks":[{"task":"do the good thing"},{"task":"park until cancelled"}]}""",
            toolCall,
            cts.Token);

        // The answering branch finishes on its single turn; only then is the run stopped.
        while (!toolCall.SubAgents![0].IsFinished)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        var result = await run;

        Assert.False(result.Success);
        Assert.Equal("Operation was cancelled.", result.ErrorMessage);
        Assert.Contains("answer from the healthy branch", result.Result);
        Assert.Contains("Cancelled: the remaining branches did not finish.", result.Result);
    }

    /// <summary>
    /// Answers when the task text does not say "park", and otherwise waits inside its stream until
    /// the run token fires.
    /// </summary>
    private sealed class AnswerOrParkGateway : ICompletionsGateway
    {
        private const string ParkingTaskMarker = "park";

        public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
            IReadOnlyList<WireMessage> messages,
            IEnumerable<Tool>? tools,
            ConversationSession targetSession,
            CompletionsResult resultCapture,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var parks = messages.Any(m =>
                m.Role == ChatMessageRole.User && m.Content?.Contains(ParkingTaskMarker) == true);

            if (parks)
                await Task.Delay(Timeout.Infinite, cancellationToken);

            yield return new ChatDelta { Content = "answer from the healthy branch" };
        }
    }

    /// <summary>
    /// Answers by BRANCH IDENTITY: the branch whose task carries the marker fails, every other one
    /// succeeds. A scripted per-turn index cannot do this - the branches run concurrently.
    /// </summary>
    private sealed class PerTaskGateway : ICompletionsGateway
    {
        private const string FailingTaskMarker = "explode";

        public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
            IReadOnlyList<WireMessage> messages,
            IEnumerable<Tool>? tools,
            ConversationSession targetSession,
            CompletionsResult resultCapture,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var fails = messages.Any(m =>
                m.Role == ChatMessageRole.User && m.Content?.Contains(FailingTaskMarker) == true);

            // The failure surfaces INSIDE the stream, exactly where the real transport raises it:
            // the service pumps this iterator from ProcessStreamAsync, which closes the turn.
            await Task.Yield();
            if (fails)
                throw new LlmApiException("provider rejected the branch");

            yield return new ChatDelta { Content = "answer from the healthy branch" };
        }
    }
}
