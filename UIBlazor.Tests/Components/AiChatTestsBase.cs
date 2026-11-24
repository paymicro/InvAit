using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.JSInterop.Infrastructure;

namespace UIBlazor.Tests.Components;

/// <summary>
/// Shared fixture of the <see cref="AiChat"/> test suite: the mocks, the session, the component
/// stubs and the stream helpers.
/// <para>
/// The suite is split into partial files by AREA (rendering, sending, the turn loop, errors,
/// persistence, auto-scroll) because one file had grown past 1800 lines and a failure in the retry
/// budget was indistinguishable from a failure in the transcript. Everything here is the part that
/// every area needs; anything used by one area only stays in that area's file.
/// </para>
/// <para>
/// A base class, not a shared helper: xUnit creates one instance per test, so the mocks and the
/// session are naturally isolated, and the derived files never have to wire the component twice.
/// </para>
/// </summary>
public abstract class AiChatTestsBase : BunitContext
{
    /// <summary>
    /// Number of turns the <see cref="StreamUntilReleased"/> factory produced. An INSTANCE field on
    /// purpose: as a static it was shared by every test of the class, so the "no new run" assertion
    /// depended on execution order.
    /// </summary>
    private int _runCount;

    protected readonly Mock<IChatService> MockChatService;
    protected readonly Mock<IToolManager> MockToolManager;
    protected readonly Mock<IProfileManager> MockProfileManager;
    protected readonly Mock<ICommonSettingsProvider> MockCommonSettings;
    protected readonly Mock<IVsBridge> MockVsBridge;
    protected readonly Mock<IMessageParser> MockMessageParser;
    protected readonly Mock<IJSRuntime> MockJsRuntime;
    protected readonly Mock<IToolCallHandler> MockToolCallHandler;
    protected readonly ConversationSession Session;
    protected readonly ProfileOptions ProfileOptions;
    protected readonly ConnectionProfile ActiveProfile;
    protected readonly CommonOptions CommonOptions;

    protected AiChatTestsBase()
    {
        MockChatService = new Mock<IChatService>();
        MockToolManager = new Mock<IToolManager>();
        MockProfileManager = new Mock<IProfileManager>();
        MockCommonSettings = new Mock<ICommonSettingsProvider>();
        MockVsBridge = new Mock<IVsBridge>();
        MockMessageParser = new Mock<IMessageParser>();
        MockJsRuntime = new Mock<IJSRuntime>();
        MockToolCallHandler = new Mock<IToolCallHandler>();

        // The turn loop owns the tool batch: approvals are prepared once and the batch runs
        // through the handler exactly once. Verified by the turn-loop tests.
        MockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        MockToolCallHandler
            .Setup(x => x.HandleApprovalAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        // Setup session with empty messages list
        Session = new ConversationSession
        {
            Id = "test-session-id",
            Mode = AppMode.Chat
        };

        // Setup profile options
        ActiveProfile = new ConnectionProfile
        {
            Id = "test-profile-id",
            Name = "Test Profile",
            TokensToCompress = 50000,
            ContextWindow = 128000,
            Stream = true
        };

        ProfileOptions = new ProfileOptions
        {
            ActiveProfileId = "test-profile-id",
            Profiles = [ActiveProfile]
        };

        // Setup common options
        CommonOptions = new CommonOptions
        {
            MaxRetries = 3,
            ShowMessageTimings = true
        };

        // Setup mock returns
        MockChatService.Setup(x => x.Session).Returns(Session);
        MockProfileManager.Setup(x => x.Current).Returns(ProfileOptions);
        MockProfileManager.Setup(x => x.ActiveProfile).Returns(ActiveProfile);
        MockCommonSettings.Setup(x => x.Current).Returns(CommonOptions);

        // Register services
        Services.AddSingleton(MockChatService.Object);
        Services.AddSingleton(MockToolManager.Object);
        Services.AddSingleton(MockProfileManager.Object);
        Services.AddSingleton(MockCommonSettings.Object);
        Services.AddSingleton(MockVsBridge.Object);
        Services.AddSingleton(MockMessageParser.Object);
        Services.AddSingleton(MockJsRuntime.Object);
        Services.AddSingleton(new UiErrorLogger(new Mock<IJSRuntime>().Object));
        Services.AddSingleton(new Mock<ILogger<AiChat>>().Object);

        Services.AddSingleton<IRetryHandler>(new RetryHandler());
        Services.AddSingleton(MockToolCallHandler.Object);
        Services.AddSingleton(new Mock<ISubAgentExecutor>().Object);
        Services.AddSingleton(new Mock<ISkillService>().Object);

        // Add Radzen components
        Services.AddRadzenComponents();

        // Setup JSInterop for Radzen and component JS calls
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
        JSInterop.SetupVoid("setChatHandler", _ => true);

        // Register component stubs
        RegisterComponentStubs();
    }

    private void RegisterComponentStubs()
    {
        // Stub for AiChatInput
        // IsLoading is projected into the DOM so a test can assert that the parent painted the
        // busy state BEFORE the first token (the Cancel button lives inside this child).
        ComponentFactories.AddStub<AiChatInput>(parameters => builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "ai-chat-input-stub");
                builder.AddAttribute(2, "data-loading", parameters.Get(p => p.IsLoading) ? "true" : "false");
                builder.AddContent(3, "AiChatInput Stub");
                builder.CloseElement();
            });

        // Stub for ChatMessageView
        ComponentFactories.AddStub<ChatMessageView>(parameters => builder =>
        {
            // Извлекаем объект сообщения из параметров
            var message = parameters.Get(p => p.Message);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "chat-message-view-stub");
            builder.AddAttribute(2, "data-message-id", message?.Id);
            builder.AddContent(3, message?.Content ?? "No content");

            // The real view offers regenerate on the last assistant bubble while idle. Mirroring it
            // here is what lets a test drive the REAL click path (renderer event dispatch, which
            // batches every StateHasChanged of the handler into ONE render).
            if (parameters.Get(p => p.IsLast) && message?.Role == ChatMessageRole.Assistant)
            {
                var onRegenerate = parameters.Get(p => p.OnRegenerate);
                builder.OpenElement(4, "button");
                builder.AddAttribute(5, "class", "chat-message-view-stub-regenerate");
                builder.AddAttribute(6, "onclick", EventCallback.Factory.Create(this, () => onRegenerate.InvokeAsync()));
                builder.CloseElement();
            }

            builder.CloseElement();
        });

        // Stub for UsageIndicators
        ComponentFactories.AddStub<UsageIndicators>(builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "usage-indicators-stub");
                builder.AddContent(2, "UsageIndicators Stub");
                builder.CloseElement();
            });

        // Stub for RecentSessionsPicker
        ComponentFactories.AddStub<RecentSessionsPicker>(builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "recent-sessions-picker-stub");
            builder.AddContent(2, "Recent Sessions Title");
            builder.CloseElement();
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Turn plumbing
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mocks <see cref="IChatService.ProcessStreamAsync"/> with the REAL semantics of one turn:
    /// content/reasoning applied to the message, tool calls mirrored into the capture, the
    /// message closed in a <c>finally</c> (also on cancellation/failure). One stream = one turn.
    /// The run stays active while the stream blocks on a release gate.
    /// </summary>
    protected void SetupProcessStreamConsume()
    {
        MockChatService.Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (VisualChatMessage msg, IAsyncEnumerable<ChatDelta> deltas,
                Action<string>? onContent, Action<List<ToolCall>>? onTools, Action? onState,
                CompletionsResult capture, CancellationToken ct) =>
            {
                msg.IsStreaming = true;
                try
                {
                    await foreach (var delta in deltas.WithCancellation(ct))
                    {
                        if (delta.Content is not null)
                        {
                            msg.Content += delta.Content;
                            onContent?.Invoke(delta.Content);
                        }

                        if (delta.ToolCalls is { Count: > 0 })
                        {
                            // AfDeltaMapper parity: the accumulated calls flow through the capture.
                            capture.AccumulatedToolCalls = [.. delta.ToolCalls];
                            msg.ToolCalls = capture.AccumulatedToolCalls;
                            onTools?.Invoke(capture.AccumulatedToolCalls);
                        }

                        onState?.Invoke();
                    }
                }
                finally
                {
                    msg.IsStreaming = false;
                }
            });
    }

    /// <summary>
    /// Scripts one stream per turn (the LAST script repeats for any further turn). Linearly
    /// enumerable sequences: a turn script may throw after yielding to simulate a transport
    /// failure mid-turn.
    /// </summary>
    protected void SetupTurnScripts(params Func<CompletionsResult, CancellationToken, IAsyncEnumerable<ChatDelta>>[] turns)
    {
        var call = 0;
        MockChatService
            .Setup(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(), It.IsAny<VisualChatMessage?>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns((ConversationSession _, VisualChatMessage? _, IReadOnlyList<Tool>? _, CompletionsResult capture, CancellationToken ct) =>
            {
                var index = Interlocked.Increment(ref call) - 1;
                capture.Model = "test-model";
                if (turns.Length == 0)
                    return AsyncEnumerable.Empty<ChatDelta>(); // a turn without payload

                return turns[Math.Min(index, turns.Length - 1)](capture, ct);
            });
    }

    /// <summary>Shortcut for <see cref="SetupTurnScripts"/>: a scripted content stream per turn.</summary>
    protected void SetupTurnStreams(params ChatDelta[][] turns)
        => SetupTurnScripts([.. turns.Select<ChatDelta[], Func<CompletionsResult, CancellationToken, IAsyncEnumerable<ChatDelta>>>(
            deltas => (_, ct) => StreamDeltasAsync(deltas, ct))]);

    /// <summary>
    /// Wires <see cref="IChatService.ProcessStreamAsync"/> to a minimal in-test pump that
    /// streams the given deltas into the message (content append + callbacks) and then
    /// throws (simulates a transport failure mid-stream). Every call (including retries)
    /// fails the same way. The content snapshot is taken AFTER the pump returns —
    /// HandleErrorAsync mutations (error overwrite) are included in it.
    /// </summary>
    protected void SetupFailingStream(List<string>? postStreamContentSnapshots, string failureMessage)
    {
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        MockChatService
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (VisualChatMessage message,
                IAsyncEnumerable<ChatDelta> deltas,
                Action<string>? onContentUpdate,
                Action<List<ToolCall>>? onToolCallsUpdate,
                Action? onStateChange,
                CompletionsResult resultCapture,
                CancellationToken cancellationToken) =>
            {
                message.IsStreaming = true;
                try
                {
                    await foreach (var delta in deltas.WithCancellation(cancellationToken))
                    {
                        if (delta.Content is not null)
                        {
                            message.Content += delta.Content;
                            onContentUpdate?.Invoke(delta.Content);
                        }

                        onStateChange?.Invoke();
                    }
                }
                finally
                {
                    // Real ProcessStreamAsync parity: the message is ALWAYS closed, also when
                    // the stream throws.
                    message.IsStreaming = false;
                    // AFTER the pump returns: HandleErrorAsync (called by the component's
                    // catch) may have overwritten the content — the snapshot includes it.
                    postStreamContentSnapshots?.Add(message.Content);
                }
            });

        SetupTurnScripts((_, ct) => CreateFailingStreamAsync(
            [new ChatDelta { Content = "Partial answer before failure" }], failureMessage, ct));
    }

    /// <summary>Runs the private <c>CancelResponseAsync</c> (the Cancel button path) on a rendered cut.</summary>
    protected static async Task CancelRunAsync(AiChat instance)
    {
        var cancelMethod = typeof(AiChat).GetMethod("CancelResponseAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)cancelMethod!.Invoke(instance, null)!;
    }

    protected static ToolCall CreateToolCall(string id, string name, string arguments = "{}")
        => new() { Id = id, Function = new ToolCallFunction { Name = name, Arguments = arguments } };

    // ═══════════════════════════════════════════════════════════════════════
    //  Stream factories
    // ═══════════════════════════════════════════════════════════════════════

    protected static async IAsyncEnumerable<ChatDelta> StreamDeltasAsync(ChatDelta[] deltas, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var delta in deltas)
        {
            ct.ThrowIfCancellationRequested();
            yield return delta;
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// A stream that delivers its deltas and then fails the way the REAL transport fails.
    /// <para>
    /// The exception type matters: retry eligibility is decided by
    /// <see cref="LlmApiException.IsRetryable"/>, and a generic exception means "a bug in our own
    /// code" — which is deliberately NOT retried. Production never surfaces a raw
    /// <c>InvalidOperationException</c> from the LLM path: <c>AfCancelRace</c> wraps every
    /// transport failure into <see cref="LlmApiException"/>. A test that threw a plain
    /// <c>Exception</c> here would assert a retry path the application cannot reach.
    /// </para>
    /// </summary>
    protected static async IAsyncEnumerable<ChatDelta> StreamThenThrowAsync(
        ChatDelta[] deltas, string failureMessage, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var delta in deltas)
        {
            yield return delta;
        }

        await Task.CompletedTask;
        throw new LlmApiException(failureMessage);
    }

    /// <summary>Fails the turn with the given exception, the way the real transport does.</summary>
    protected static async IAsyncEnumerable<ChatDelta> ThrowLlmAsync(
        Exception exception, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162 // Unreachable: keeps this method an async iterator (throw on MoveNext).
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>
    /// Fails the turn with the exception the real transport produces (see
    /// <see cref="StreamThenThrowAsync"/> for why the type is part of the contract).
    /// </summary>
    protected static async IAsyncEnumerable<ChatDelta> CreateFailingStreamAsync(
        ChatDelta[] deltas, string failureMessage,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var delta in deltas)
        {
            yield return delta;
        }

        throw new LlmApiException(failureMessage);
    }

    /// <summary>Delivers nothing until <paramref name="release"/> completes (an open run).</summary>
    protected static async IAsyncEnumerable<ChatDelta> BlockedStream(
        TaskCompletionSource release, [EnumeratorCancellation] CancellationToken ct)
    {
        await release.Task.WaitAsync(ct);
        yield break;
    }

    /// <summary>
    /// An update stream that delivers nothing until <paramref name="gate"/> completes: models a
    /// request whose first token is still pending.
    /// </summary>
    protected static async IAsyncEnumerable<ChatDelta> BlockingStreamAsync(Task gate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        yield break;
    }

    /// <summary>Streams one content delta, then blocks until <paramref name="gate"/> completes.</summary>
    protected static async IAsyncEnumerable<ChatDelta> PartialThenBlockAsync(Task gate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ChatDelta { Content = "partial" };
        await gate.WaitAsync(cancellationToken);
    }

    /// <summary>Streams one tool call, then blocks until <paramref name="gate"/> completes.</summary>
    protected static async IAsyncEnumerable<ChatDelta> ToolCallThenBlockAsync(ToolCall toolCall, Task gate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ChatDelta { ToolCalls = [toolCall] };
        await gate.WaitAsync(cancellationToken);
    }

    /// <summary>An open run that counts itself in <see cref="_runCount"/> (see the field's remark).</summary>
    protected async IAsyncEnumerable<ChatDelta> StreamUntilReleased(
        TaskCompletionSource release, CompletionsResult capture, [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref _runCount);
        capture.Model = "test-model";
        yield return new ChatDelta { Content = "streaming..." };
        await release.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Local variant of <see cref="StreamUntilReleased"/> without the run counter: tests that
    /// assert their own counter must not disturb the "no new run" assertion.
    /// </summary>
    protected static async IAsyncEnumerable<ChatDelta> StreamUntilReleasedLocal(
        TaskCompletionSource release, CompletionsResult capture, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ChatDelta { Content = "streaming..." };
        await release.Task.WaitAsync(ct);
    }

    /// <summary>Turns produced by <see cref="StreamUntilReleased"/> in THIS test.</summary>
    protected int RunCount => _runCount;

    // ═══════════════════════════════════════════════════════════════════════
    //  JS observation
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Captures the arguments of every <c>InvokeVoidAsync</c> call to <paramref name="identifier"/>.
    /// <para>
    /// <c>InvokeVoidAsync</c> resolves to <c>InvokeAsync&lt;IJSVoidResult&gt;</c>, so a test that
    /// wants to observe a cosmetic JS call has to mock that exact overload — and
    /// <c>IJSVoidResult</c> lives in <c>Microsoft.JSInterop.Infrastructure</c>. Keeping the setup
    /// here means that namespace is needed by ONE file (this fixture) instead of by every test area
    /// that asserts a scroll, and the three near-identical Moq setups collapse into one.
    /// </para>
    /// </summary>
    /// <returns>A live list: it grows as the component makes the calls.</returns>
    protected List<object?[]> CaptureJsVoidCalls(string identifier)
    {
        var calls = new List<object?[]>();
        MockJsRuntime
            .Setup(js => js.InvokeAsync<IJSVoidResult>(identifier, It.IsAny<CancellationToken>(), It.IsAny<object?[]>()))
            .Callback<string, CancellationToken, object?[]>((_, _, args) => calls.Add(args))
            .Returns(ValueTask.FromResult<IJSVoidResult>(null!));
        return calls;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Waiting
    // ═══════════════════════════════════════════════════════════════════════

    protected static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail("Condition was not met within the timeout");
            await Task.Delay(10);
        }
    }
}
