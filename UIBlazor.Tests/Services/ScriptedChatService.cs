using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Runs the REAL <see cref="ChatService"/> against a scripted gateway.
/// <para>
/// Why this exists: the turn loop moved to the caller, so the orchestrator and <c>AIChat</c>
/// drive <see cref="ChatService.ProcessStreamAsync"/> themselves. A test stub that re-implements
/// <c>ProcessStreamAsync</c> therefore tests its own copy of the production logic — the delta
/// application order, the message close in <c>finally</c>, the timings, the empty-payload skip.
/// Here the production implementation runs and only the LLM transport is scripted, so every
/// assertion lands on shipping code.
/// </para>
/// </summary>
internal sealed class ScriptedChatService : IDisposable
{
    private readonly Mock<ILocalStorageService> _localStorage = new();
    private readonly Mock<IProfileManager> _profileManager = new();

    private ScriptedChatService(ChatService service, ScriptedChatGateway gateway)
    {
        Service = service;
        Gateway = gateway;
    }

    /// <summary>The real service under test.</summary>
    public ChatService Service { get; }

    /// <summary>The scripted transport behind <see cref="ChatService.ChatGateway"/>.</summary>
    public ScriptedChatGateway Gateway { get; }

    /// <summary>Number of LLM turns the service has started.</summary>
    public int Calls => Gateway.Calls;

    /// <summary>
    /// Builds a service whose LLM turns go through the REAL <see cref="AfCompletionsAdapter"/>
    /// against <paramref name="profile"/>'s endpoint (e.g. a WireMock server). Neither gateway
    /// is substituted, so the whole stack runs: request build → transport → delta mapping →
    /// <c>ProcessStreamAsync</c>.
    /// </summary>
    /// <param name="profile">Provider profile pointing at the test endpoint.</param>
    /// <param name="tools">Tools reported as enabled for every mode.</param>
    /// <param name="systemPrompt">Text returned by the mocked <see cref="ISystemPromptBuilder"/>.</param>
    public static ScriptedChatService CreateWithRealAdapter(
        ConnectionProfile? profile = null,
        IReadOnlyList<Tool>? tools = null,
        string systemPrompt = "sub prompt",
        IProfileManager? profileManager = null,
        ICommonSettingsProvider? commonSettings = null)
        => Create(
            turn: static (_, _) => throw new InvalidOperationException(
                "CreateWithRealAdapter does not script turns: the real adapter is the transport."),
            profile: profile,
            tools: tools,
            systemPrompt: systemPrompt,
            useRealGateways: true,
            profileManager: profileManager,
            commonSettings: commonSettings);

    /// <summary>
    /// Builds a service whose LLM turns are produced by <paramref name="turn"/>: called with the
    /// zero-based turn index and the turn's capture, it returns the deltas of ONE turn.
    /// </summary>
    /// <param name="turn">Per-turn delta script.</param>
    /// <param name="profile">Provider profile; a permissive one by default (no compression).</param>
    /// <param name="tools">Tools reported as enabled for every mode (a branch passes its own list).</param>
    /// <param name="systemPrompt">Text returned by the mocked <see cref="ISystemPromptBuilder"/>.</param>
    /// <param name="contentFilter">Optional tool-result filter (real one by default: no filtering).</param>
    /// <param name="useRealGateways">Keep the service's own adapter (no scripting).</param>
    internal static ScriptedChatService Create(
        Func<int, CompletionsResult, IAsyncEnumerable<ChatDelta>> turn,
        ConnectionProfile? profile = null,
        IReadOnlyList<Tool>? tools = null,
        string systemPrompt = "sub prompt",
        IContentFilter? contentFilter = null,
        bool useRealGateways = false,
        IProfileManager? profileManager = null,
        ICommonSettingsProvider? commonSettings = null)
    {
        var gateway = new ScriptedChatGateway(turn);

        // The caller may own the profile manager (a test that flips TokensToCompress per case
        // must affect BOTH the orchestrator's gate and this service's adapter).
        if (profileManager is null)
        {
            var owned = new Mock<IProfileManager>();
            owned
                .SetupGet(p => p.ActiveProfile)
                .Returns(profile ?? new ConnectionProfile
                {
                    Endpoint = "http://localhost:1",
                    ApiKey = "key",
                    Model = "test-model",
                    // Compression OFF by default: the branch gate reads TokensToCompress, and a
                    // scripted turn has no provider usage - a non-zero threshold would compress
                    // on accumulate-only estimates and change what these tests observe.
                    TokensToCompress = 0
                });
            profileManager = owned.Object;
        }

        // The adapter reads HttpTimeoutSec through this provider on every request: a bare
        // mock would hand back null and dereference it inside the timeout lambda.
        if (commonSettings is null)
        {
            var ownedSettings = new Mock<ICommonSettingsProvider>();
            ownedSettings.SetupGet(s => s.Current).Returns(new CommonOptions());
            commonSettings = ownedSettings.Object;
        }

        var localStorage = new Mock<ILocalStorageService>();
        localStorage
            .Setup(ls => ls.SetItemAsync(It.IsAny<string>(), It.IsAny<ConversationSession>()))
            .Returns(Task.CompletedTask);
        localStorage.Setup(ls => ls.GetAllKeysAsync()).ReturnsAsync([]);

        var promptBuilder = new Mock<ISystemPromptBuilder>();
        promptBuilder
            .Setup(b => b.PrepareSystemPromptAsync(It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(systemPrompt);
        promptBuilder
            .Setup(b => b.PrepareSubAgentSystemPromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(systemPrompt);

        var toolManager = new Mock<IToolManager>();
        toolManager
            .Setup(t => t.GetEnabledTools(It.IsAny<AppMode>()))
            .Returns(tools ?? []);

        var service = new ChatService(
            new HttpClient(),
            profileManager,
            promptBuilder.Object,
            localStorage.Object,
            NullLogger<IChatService>.Instance,
            toolManager.Object,
            contentFilter ?? Mock.Of<IContentFilter>(),
            commonSettings,
            Mock.Of<IToolCallHandler>())
        {
            // Autosave would fire a storage write per mutation; irrelevant to these tests.
            AutosaveDelay = TimeSpan.FromSeconds(30)
        };

        if (!useRealGateways)
        {
            service.ChatGateway = gateway;
            service.CompressionGateway = gateway;
        }

        return new ScriptedChatService(service, gateway);
    }

    public void Dispose() => Service.Dispose();
}

/// <summary>
/// Scripted <see cref="ICompletionsGateway"/>: records every request and replays the deltas
/// produced by the per-turn delegate. It is the ONLY substituted layer — request assembly,
/// delta application and message lifecycle all run through production code.
/// </summary>
internal sealed class ScriptedChatGateway(Func<int, CompletionsResult, IAsyncEnumerable<ChatDelta>> turn)
    : ICompletionsGateway
{
    /// <summary>Number of turns started (a throwing turn still counts: it was attempted).</summary>
    public int Calls { get; private set; }

    /// <summary>Request history, one entry per turn: the exact wire the service built.</summary>
    public List<IReadOnlyList<WireMessage>> Requests { get; } = [];

    /// <summary>Tools passed with each turn request.</summary>
    public List<IReadOnlyList<Tool>?> RequestTools { get; } = [];

    public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
        IReadOnlyList<WireMessage> messages,
        IEnumerable<Tool>? tools,
        ConversationSession targetSession,
        CompletionsResult resultCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var index = Calls++;
        Requests.Add(messages);
        RequestTools.Add(tools as IReadOnlyList<Tool> ?? tools?.ToList());

        await foreach (var delta in turn(index, resultCapture).WithCancellation(cancellationToken))
        {
            yield return delta;
        }
    }
}

/// <summary>
/// Composers for the turn scripts used by the orchestrator / turn-loop tests.
/// <para>
/// Every script is an ASYNC ITERATOR and yields before doing anything observable: the service
/// receives the stream as an argument to <c>ProcessStreamAsync</c>, so a script that threw
/// synchronously would escape before the message could be closed.
/// </para>
/// </summary>
internal static class Turns
{
    /// <summary>One assistant turn carrying <paramref name="content"/>.</summary>
    public static Func<int, CompletionsResult, IAsyncEnumerable<ChatDelta>> Text(string content)
        => (_, _) => Script(content: content);

    /// <summary>One assistant turn per entry of <paramref name="contents"/> (turn index selects).</summary>
    public static Func<int, CompletionsResult, IAsyncEnumerable<ChatDelta>> Sequence(params string[] contents)
        => (index, _) => Script(content: contents[Math.Min(index, contents.Length - 1)]);

    /// <summary>
    /// A reasoning-only update followed by a text update. Two seams, both inside the stream:
    /// <paramref name="onBeforeReasoning"/> runs BEFORE the reasoning update is handed over
    /// (nothing of it is applied yet), <paramref name="onReasoningApplied"/> runs after the
    /// service applied it and invoked <c>onStateChange</c>, before the next update is requested.
    /// Comparing state captured at the two seams isolates the effect of that ONE delta.
    /// </summary>
    public static Func<int, CompletionsResult, IAsyncEnumerable<ChatDelta>> ReasoningThenText(
        string reasoning,
        string content,
        Func<Task> onReasoningApplied,
        Func<Task>? onBeforeReasoning = null)
        => (_, _) => Script(content: content, reasoning: reasoning, after: onReasoningApplied, before: onBeforeReasoning);

    /// <summary>
    /// A turn producing <paramref name="calls"/>. <see cref="CompletionsResult.AccumulatedToolCalls"/>
    /// is set the way the real mapper sets it: the delta carries the SAME list instance, which is
    /// what tells <c>ProcessStreamAsync</c> the calls are ready to be mirrored.
    /// </summary>
    public static Func<int, CompletionsResult, IAsyncEnumerable<ChatDelta>> ToolCalls(
        params ToolCall[] calls)
        => (_, capture) => ScriptWithCalls(calls, capture);

    /// <summary>A turn that fails at the transport level (throws while being enumerated).</summary>
    public static Func<int, CompletionsResult, IAsyncEnumerable<ChatDelta>> Failure(
        string message = "transient failure")
        => (_, _) => Root(message);

    /// <summary>
    /// A turn that fails with a specific HTTP status — the shape a provider rejection takes in
    /// production (<see cref="LlmApiException.ForHttpStatus"/>).
    /// </summary>
    public static Func<int, CompletionsResult, IAsyncEnumerable<ChatDelta>> HttpStatus(
        int statusCode, string message)
        => (_, _) => Root(message, LlmApiException.ForHttpStatus(statusCode, message));

    /// <summary>
    /// A turn that fails with an arbitrary exception — used to prove that a failure which is NOT
    /// <see cref="LlmApiException"/> is never retried.
    /// </summary>
    public static Func<int, CompletionsResult, IAsyncEnumerable<ChatDelta>> Throw(Exception exception)
        => (_, _) => Root(exception.Message, exception);

    /// <summary>Fails the first <paramref name="failures"/> turns, then answers.</summary>
    public static Func<int, CompletionsResult, IAsyncEnumerable<ChatDelta>> FailFirst(
        int failures,
        string content = "sub-agent answer")
        => (index, _) => index < failures ? Root("transient failure") : Script(content: content);

    /// <summary>
    /// The first <paramref name="empties"/> turns deliver NOTHING and then answer. The shape is the
    /// real one: a provider that answers with an empty delta sends no payload at all, which the
    /// mapper drops (Map returns null), so the caller's stream simply ends without a single update.
    /// <para>
    /// The index is the ATTEMPT index, not the answer index: an empty turn still consumes one (it is
    /// a real LLM call). A <see cref="Sequence"/> placed after an empty turn therefore starts at the
    /// second entry, not the first.
    /// </para>
    /// </summary>
    public static Func<int, CompletionsResult, IAsyncEnumerable<ChatDelta>> EmptyFirst(
        int empties,
        string content = "sub-agent answer")
        => (index, _) => index < empties ? Script(content: null) : Script(content: content);

    /// <summary>
    /// Every turn carries reasoning and no text — the shape of a model that thinks and then stops.
    /// It is a legitimate answer for the retry rule (reasoning is payload), so the branch must not
    /// spend its budget on it.
    /// </summary>
    public static Func<int, CompletionsResult, IAsyncEnumerable<ChatDelta>> ReasoningOnly(
        string reasoning = "thinking it through")
        => (_, _) => Script(content: null, reasoning: reasoning);

    private static async IAsyncEnumerable<ChatDelta> Script(
        string? content,
        string? reasoning = null,
        Func<Task>? after = null,
        Func<Task>? before = null)
    {
        await Task.Yield();

        if (reasoning is not null)
        {
            if (before is not null)
            {
                await before();
            }

            yield return new ChatDelta { ReasoningContent = reasoning };
        }

        if (after is not null)
        {
            await after();
        }

        if (content is not null)
        {
            yield return new ChatDelta { Content = content };
        }
    }

    private static async IAsyncEnumerable<ChatDelta> ScriptWithCalls(
        ToolCall[] calls,
        CompletionsResult capture)
    {
        await Task.Yield();

        // Mirrors AfDeltaMapper: the capture owns the accumulated list, the delta references it.
        capture.AccumulatedToolCalls = [.. calls];
        yield return new ChatDelta { ToolCalls = capture.AccumulatedToolCalls };
    }

    /// <summary>
    /// Fails the turn the way the REAL transport fails. The type is part of the contract: only
    /// <see cref="LlmApiException"/> is eligible for a retry, a generic exception means a bug in
    /// our code and must not spend the budget — see <see cref="LlmApiException.IsRetryable"/>.
    /// </summary>
    private static async IAsyncEnumerable<ChatDelta> Root(string message, Exception? exception = null)
    {
        await Task.Yield();
        throw exception ?? new LlmApiException(message);
#pragma warning disable CS0162 // Unreachable: keeps this method an async iterator (throw on MoveNext).
        yield break;
#pragma warning restore CS0162
    }
}

/// <summary>
/// Transparent recording decorator over a REAL <see cref="IChatService"/>.
/// <para>
/// Some behaviour is only observable from the outside of the service — "the branch gate decided
/// to compress" is a CALL, not an effect (a short branch history may legitimately collapse
/// nothing, so <see cref="IChatService.CompressionStarted"/> would stay silent). This decorator
/// records those calls and forwards EVERYTHING else to the real service: the delta application,
/// the message lifecycle and the wire assembly still run through production code, unlike the
/// hand-written stubs it replaces.
/// </para>
/// </summary>
internal sealed class RecordingChatService(IChatService inner) : IChatService
{
    /// <summary>Number of <c>CompressSessionAsync(targetSession, ...)</c> calls.</summary>
    public int CompressCalls { get; private set; }

    /// <summary>Ids of the sessions passed to the branch compression overload.</summary>
    public List<string> CompressedSessionIds { get; } = [];

    /// <summary>
    /// Value of the sub-agent's <c>IsCompressing</c> flag observed AT CALL TIME (the orchestrator
    /// raises it before the call and lowers it after) — proves the flag spans the call only.
    /// </summary>
    public bool IsCompressingDuringCall { get; private set; }

    /// <summary>Resolves the branch the orchestrator is running, when a test needs it.</summary>
    public Func<SubAgentMessage?>? SubAgentProvider { get; set; }

    public ConversationSession Session => inner.Session;

    public event PropertyChangedEventHandler? SessionChanged
    {
        add => inner.SessionChanged += value;
        remove => inner.SessionChanged -= value;
    }

    public event Action<CompressionReport>? CompressionCompleted
    {
        add => inner.CompressionCompleted += value;
        remove => inner.CompressionCompleted -= value;
    }

    public event Action<ConversationSession>? CompressionStarted
    {
        add => inner.CompressionStarted += value;
        remove => inner.CompressionStarted -= value;
    }

    public void Initialize() => inner.Initialize();

    public bool NeedsCompression(ConversationSession targetSession) => inner.NeedsCompression(targetSession);

    public IAsyncEnumerable<ChatDelta> GetTurnCompletionsAsync(
        ConversationSession targetSession,
        VisualChatMessage? live,
        IReadOnlyList<Tool>? tools,
        CompletionsResult resultCapture,
        CancellationToken cancellationToken)
        => inner.GetTurnCompletionsAsync(targetSession, live, tools, resultCapture, cancellationToken);

    public Task ProcessStreamAsync(
        VisualChatMessage message,
        IAsyncEnumerable<ChatDelta> deltas,
        Action<string>? onContentUpdate,
        Action<List<ToolCall>> onToolCallsUpdate,
        Action? onStateChange,
        CompletionsResult resultCapture,
        CancellationToken cancellationToken)
        => inner.ProcessStreamAsync(
            message, deltas, onContentUpdate, onToolCallsUpdate, onStateChange, resultCapture, cancellationToken);

    public IAsyncEnumerable<ChatDelta> GetCompletionsAsync(CompletionsResult resultCapture, CancellationToken cancellationToken)
        => inner.GetCompletionsAsync(resultCapture, cancellationToken);

    public IAsyncEnumerable<ChatDelta> CompressSessionAsync(CompletionsResult resultCapture, CancellationToken cancellationToken)
        => inner.CompressSessionAsync(resultCapture, cancellationToken);

    public IAsyncEnumerable<ChatDelta> CompressSessionAsync(
        ConversationSession targetSession, CompletionsResult resultCapture, CancellationToken cancellationToken)
    {
        // Recorded at CALL time, not on enumeration: the orchestrator owns the flag around the
        // call, and a lazy stream would observe it after it was already lowered.
        CompressCalls++;
        CompressedSessionIds.Add(targetSession.Id);
        IsCompressingDuringCall = SubAgentProvider?.Invoke()?.IsCompressing ?? false;

        return inner.CompressSessionAsync(targetSession, resultCapture, cancellationToken);
    }

    public Task<List<SessionSummary>> GetRecentSessionsAsync(int count) => inner.GetRecentSessionsAsync(count);

    public Task LoadSessionAsync(string id) => inner.LoadSessionAsync(id);

    public Task DeleteSessionAsync(string id) => inner.DeleteSessionAsync(id);

    public Task SaveSessionAsync() => inner.SaveSessionAsync();

    public Task NewSessionAsync() => inner.NewSessionAsync();

    public Task<AiModelList> GetModelsAsync(CancellationToken cancellationToken) => inner.GetModelsAsync(cancellationToken);

    public void Dispose() => inner.Dispose();
}
