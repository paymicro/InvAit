using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;

namespace UIBlazor.Services;

public class ChatService(
    HttpClient httpClient,
    IProfileManager profileManager,
    ISystemPromptBuilder systemPromptBuilder,
    ILocalStorageService localStorage,
    ILogger<IChatService> logger,
    IToolManager toolManager,
    IContentFilter contentFilter,
    ICommonSettingsProvider commonSettingsProvider,
    IToolCallHandler toolCallHandler
    ) : IChatService
{
    /// <summary>Tool results longer than this are stubbed out of history during compression
    /// ( "~4000 chars ≈ 1K tokens"); the full result no longer fits the compressed context.</summary>
    private const int CompressedToolResultThreshold = 4000;

    // LLM gateway: a SINGLE-SHOT IChatClient call with no automatic tool execution and no
    // hidden turn loop. The turn cycle lives in the caller (AiChat / AfSubAgentOrchestrator),
    // which calls this gateway once per turn and executes the tools in between.
    private readonly AfCompletionsAdapter _afAdapter =
        new(() => profileManager.ActiveProfile, httpClient, () => commonSettingsProvider.Current.HttpTimeoutSec);

    private ICompletionsGateway? _compressionGatewayOverride;
    private ICompletionsGateway? _chatGatewayOverride;

    /// <summary>
    /// Gateway used by context compression (single-shot, no tools). Defaults to
    /// <see cref="_afAdapter"/>; tests substitute a deterministic summary stream.
    /// </summary>
    internal ICompletionsGateway CompressionGateway
    {
        get => _compressionGatewayOverride ?? _afAdapter;
        set => _compressionGatewayOverride = value;
    }

    /// <summary>
    /// Gateway used by the per-turn chat seam (<see cref="GetTurnCompletionsAsync"/>). Defaults
    /// to <see cref="_afAdapter"/>; tests substitute a scripted stream to observe the request
    /// the seam builds without a transport.
    /// </summary>
    internal ICompletionsGateway ChatGateway
    {
        get => _chatGatewayOverride ?? _afAdapter;
        set => _chatGatewayOverride = value;
    }

    public ConnectionProfile Options => profileManager.ActiveProfile;

    /// <summary>
    /// Подписка на события после создания экземпляра
    /// </summary>
    public void Initialize() => EnsureWired();

    private bool _wired;

    /// <summary>
    /// Idempotent one-time wiring of the invariants that must never depend on app startup order:
    /// the autosave of the current session. Called from <see cref="Initialize"/> and lazily from
    /// the <see cref="Session"/> getter, so a host that skips Initialize cannot silently lose
    /// persistence.
    /// </summary>
    private void EnsureWired()
    {
        if (_wired)
            return;
        _wired = true;

        Session.PropertyChanged -= SessionPropertyChanged;
        Session.PropertyChanged += SessionPropertyChanged;
        AttachAutosave(Session);
    }

    public ConversationSession Session
    {
        get
        {
            // Lazy guarantee: the persistence and mode-refresh invariants are wired even if the
            // host never called Initialize (idempotent, single bool check on a hot path).
            EnsureWired();
            return field;
        }
        private set
        {
            if (field == value)
                return;
            field?.PropertyChanged -= SessionPropertyChanged;
            field = value;
            field?.PropertyChanged += SessionPropertyChanged;
            AttachAutosave(field);
            SessionChanged?.Invoke(field, new PropertyChangedEventArgs(nameof(ConversationSession)));
        }
    } = CreateNewSession();

    public event PropertyChangedEventHandler? SessionChanged;

    private void SessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => SessionChanged?.Invoke(sender, e);

    // ── Autosave ───────────────────────────────────────────────────────────
    // Saving is an invariant of the session model, not a duty of every caller:
    // ConversationSession raises Changed on structural mutations and this pump persists them.
    // Callers never *have* to remember SaveSessionAsync — forgetting it can no longer lose work.
    //
    // Guarantees:
    //  - burst coalescing: many mutations within AutosaveDelay produce ONE write;
    //  - writes are serialized (localStorage interop must not interleave);
    //  - a failed write is logged and retried by the next mutation/explicit flush;
    //  - SaveSessionAsync() flushes immediately (explicit "persist now" hook).

    private readonly object _autosaveSync = new();
    private readonly HashSet<ConversationSession> _dirtySessions = [];
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _autosaveDebounce;
    private ConversationSession? _autosaveAttached;

    /// <summary>Debounce window before a scheduled autosave write. Tests set it to zero.</summary>
    internal TimeSpan AutosaveDelay { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Subscribes the autosave pump to <paramref name="session"/>. Only the current session is
    /// attached: callers persist the previous one explicitly before swapping, and a swap is
    /// blocked while an agent run is in flight (the run pins its session).
    /// </summary>
    private void AttachAutosave(ConversationSession? session)
    {
        if (session is null || ReferenceEquals(_autosaveAttached, session))
            return;

        if (_autosaveAttached is not null)
            _autosaveAttached.Changed -= OnSessionChanged;

        _autosaveAttached = session;
        session.Changed += OnSessionChanged;
    }

    private void DetachAutosave(ConversationSession session)
    {
        session.Changed -= OnSessionChanged;
        if (ReferenceEquals(_autosaveAttached, session))
            _autosaveAttached = null;

        lock (_autosaveSync)
            _dirtySessions.Remove(session);
    }

    private void OnSessionChanged(ConversationSession session)
    {
        CancellationToken token;
        lock (_autosaveSync)
        {
            _dirtySessions.Add(session);
            _autosaveDebounce?.Cancel();
            _autosaveDebounce?.Dispose();
            _autosaveDebounce = new CancellationTokenSource();
            token = _autosaveDebounce.Token;
        }

        _ = AutosaveAfterDelayAsync(token);
    }

    private async Task AutosaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (AutosaveDelay > TimeSpan.Zero)
                await Task.Delay(AutosaveDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // a newer mutation re-scheduled the write
        }

        try
        {
            await SaveSessionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let a background save take the app down.
            logger.LogError("Autosave failed: {Message}", ex.Message);
        }
    }

    public async Task<AiModelList> GetModelsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(Options.Endpoint))
        {
            throw new InvalidOperationException("Endpoint must be specified.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ConnectionProfile.NormalizeApiBase(Options.Endpoint)}/models");

        LlmAuthHeader.Apply(request, Options);

        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Getting models failed: {await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");
        }

        return await response.Content.ReadFromJsonAsync<AiModelList>(cancellationToken)
               ?? throw new JsonException("Models deserialization exception");
    }

    /// <inheritdoc />
    public bool NeedsCompression(ConversationSession targetSession)
        => Options.TokensToCompress > 0 && targetSession.TotalTokens > Options.TokensToCompress;

    /// <inheritdoc />
    public event Action<CompressionReport>? CompressionCompleted;

    /// <inheritdoc />
    public event Action<ConversationSession>? CompressionStarted;

    /// <summary>
    /// Pumps exactly ONE LLM turn into the given message: every delta is applied monotonically
    /// (content/reasoning appended, tool calls mirrored) and the message is ALWAYS closed
    /// (<c>IsStreaming = false</c> + final timings) - cancellation and failure included.
    /// <para>
    /// The stream is ONE turn: the turn cycle belongs to the caller (<c>AIChat</c> /
    /// <c>AfSubAgentOrchestrator</c>), which calls the gateway once per turn and owns the
    /// per-turn message.
    /// </para>
    /// </summary>
    public async Task ProcessStreamAsync(
        VisualChatMessage message,
        IAsyncEnumerable<ChatDelta> deltas,
        Action<string>? onContentUpdate,
        Action<List<ToolCall>>? onToolCallsUpdate,
        Action? onStateChange,
        CompletionsResult resultCapture,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var reasoning = new StringBuilder();
        var response = new StringBuilder();
        double firstTokenMs = 0;
        double firstContentTokenMs = 0;

        message.Timings ??= new MessageTimings { Tokens = 0 };

        try
        {
            await foreach (var delta in deltas.WithCancellation(cancellationToken))
            {
                var hasPayload = !string.IsNullOrEmpty(delta.Content)
                                 || !string.IsNullOrEmpty(delta.ReasoningContent)
                                 || delta.ToolCalls is { Count: > 0 };

                if (!hasPayload)
                {
                    // Empty update (usage-only chunk, legacy turn marker): nothing to render.
                    onStateChange?.Invoke();
                    continue;
                }

                if (firstTokenMs == 0)
                    firstTokenMs = sw.ElapsedMilliseconds;

                if (!string.IsNullOrEmpty(delta.ReasoningContent))
                {
                    reasoning.Append(delta.ReasoningContent);
                    message.ReasoningContent = reasoning.ToString();
                }

                // Первый ВИДИМЫЙ payload (content или tool_calls — reasoning не в счёт) —
                // начало сегмента "Reasoning" таймингов. Дублирующиеся проверки в обеих
                // ветках payload слиты в одну.
                var hasVisibleContent = !string.IsNullOrEmpty(delta.Content)
                                        || (delta.ToolCalls is { Count: > 0 } && resultCapture.AccumulatedToolCalls is not null);
                if (firstContentTokenMs == 0 && hasVisibleContent)
                {
                    firstContentTokenMs = sw.ElapsedMilliseconds;
                    message.Timings.Reasoning = TimeSpan.FromMilliseconds(firstContentTokenMs - firstTokenMs);
                }

                if (!string.IsNullOrEmpty(delta.Content))
                {
                    response.Append(delta.Content);
                    message.Content = response.ToString();
                    onContentUpdate?.Invoke(delta.Content);
                }

                if (delta.ToolCalls is { Count: > 0 } && resultCapture.AccumulatedToolCalls is not null)
                {
                    onToolCallsUpdate?.Invoke(resultCapture.AccumulatedToolCalls);
                }

                if (firstContentTokenMs > 0)
                {
                    message.Timings.Content = TimeSpan.FromMilliseconds(sw.ElapsedMilliseconds - firstContentTokenMs);
                }

                // Динамический подсчет таймингов для UI
                CalcTimings(message, sw, firstTokenMs, resultCapture);

                onStateChange?.Invoke();
            }
        }
        finally
        {
            // Финальный подсчет таймингов. Нужен т.к. токен с Usage не запускает цикл выше (он без дельты).
            // Выполняется ВСЕГДА, включая отмену/исключение: незакрытое сообщение навсегда
            // выпадает из wire-истории (фильтр IsStreaming) и висит замороженным пузырём.
            // IsStreaming сбрасывается ДО расчета: завершенный бейдж должен показать видимые токены.
            message.IsStreaming = false;
            CalcTimings(message, sw, firstTokenMs, resultCapture);
        }
    }

    private static void CalcTimings(VisualChatMessage message, Stopwatch sw, double firstTokenMs, CompletionsResult completionsResult)
    {
        var elapsedMs = sw.ElapsedMilliseconds;

        // Бейдж всегда показывает ПОЛНЫЙ вывод (включая reasoning): это счетчик генерации,
        // а позиция в контексте отслеживается отдельно через Session.TotalTokens
        // (VisibleCompletionTokens - reasoning не досылается).
        // Смена значения в конце стрима выглядела бы как баг: "токенов стало меньше".
        message.Timings.Tokens = completionsResult.CompletionTokens;
        message.Timings.ReasoningTokens = completionsResult.ReasoningTokens;

        // Скорость генерации честнее считать по полному выводу, включая размышления.
        var secForTokens = Math.Max(1, (elapsedMs - firstTokenMs) / 1000.0);
        message.Timings.TokensInSec = (float)(completionsResult.CompletionTokens / secForTokens);
        message.Timings.Total = TimeSpan.FromMilliseconds(elapsedMs);
        message.Timings.FirstToken = TimeSpan.FromMilliseconds(firstTokenMs);
    }


    /// <summary>
    /// Сжатие контекста указанной сессии (например, ветки субагента): голова и хвост истории
    /// остаются дословно, середина заменяется сводкой от LLM НА МЕСТЕ (CollapseMessages) —
    /// живые сообщения запуска не теряют идентичность (resultCapture — состояние LLM-вызова).
    /// </summary>
    private async IAsyncEnumerable<ChatDelta> CompressSessionCoreAsync(
        ConversationSession targetSession,
        CompletionsResult resultCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Snapshot BEFORE the LLM call: the plan and the summarizer prompt must describe the
        // SAME history even if the user appends a message (interject) while the summary is built.
        var snapshot = targetSession.GetMessagesSnapshot();
        var collapse = PlanCompression(snapshot);

        // Nothing worth summarizing: do not call the LLM at all. The old code created a summary
        // unconditionally, so a short history degenerated into [head][tail][summary] noise while
        // the token threshold stayed armed (the next message compressed again).
        if (collapse.Count == 0)
            yield break;

        CompressionStarted?.Invoke(targetSession);

        var (Messages, _) = targetSession.GetFormattedMessagesForCompress(contentFilter);

        // Получаем сжатый текст от LLM (compression runs WITHOUT tools - single-shot adapter)
        var contentSb = new StringBuilder();
        resultCapture.Reset();
        await foreach (var chatDelta in CompressionGateway.GetCompletionsAsync(Messages, null, targetSession, resultCapture, cancellationToken))
        {
            if (chatDelta.Content is not null)
            {
                contentSb.Append(chatDelta.Content);
            }
            yield return chatDelta;
        }

        if (cancellationToken.IsCancellationRequested)
            yield break;

        var summaryContent = contentSb.ToString();

        // An empty summary must never destroy the history: keep the compressed part verbatim.
        if (string.IsNullOrWhiteSpace(summaryContent))
            yield break;

        // Создаем новый объект сообщения со сжатым контекстом
        var compressedMessage = new VisualChatMessage
        {
            Content = summaryContent,
            Role = ChatMessageRole.Assistant,
            IsExpanded = true
        };

        // Assistant messages are rendered from Segments ONLY: a summary without them showed up
        // as an empty bubble ("invisible goal message") whenever compression ran outside the
        // manual /compact command (mid-run gate, sub-agent branch).
        MessageParser.BuildHistorySegments(summaryContent, compressedMessage);

        // Stub the oversized tool results of the KEPT messages: the collapsed part is gone, but
        // the verbatim head/tail still carries payloads that no longer fit the compressed context.
        var collapsed = new HashSet<VisualChatMessage>(collapse, ReferenceEqualityComparer.Instance);
        StubLongToolResults(snapshot.Where(m => !collapsed.Contains(m)));

        // Collapse IN PLACE: only the planned middle is replaced by the summary. Every other
        // message keeps its identity — the live streaming message of the running turn included,
        // which the agent loop keeps writing into (a whole-list replacement orphaned it: the UI
        // stopped updating and the generation looked frozen).
        if (!targetSession.CollapseMessages(collapse, compressedMessage))
            yield break;

        CompressionCompleted?.Invoke(new CompressionReport(targetSession, compressedMessage, collapse.Count));

        // Re-arm the compression threshold: the context just shrank, so TotalTokens must follow
        // the kept history. Without this the mid-run gate (checked before every LLM call) sees
        // "still over the limit" and re-summarizes the already compressed history at every turn
        // boundary — an extra LLM call per turn plus a destructive history rewrite on live turns.
        // The tool schemas ride along with every request, so they belong to the re-armed value.
        targetSession.TotalTokens = EstimateContextTokens(
            targetSession.GetMessagesSnapshot(),
            toolManager.GetEnabledTools(targetSession.Mode));
    }

    /// <summary>
    /// Plans the in-place compression: which messages the LLM summary replaces.
    /// Layout: [head window][summary][tail window]. Only VISIBLE messages are considered —
    /// an assistant turn without payload can never occupy a window slot (legacy empty-turn
    /// bubbles used to fill the head/tail windows, so the visible history looked broken).
    /// The tail keeps the newest messages verbatim and NEVER the live streaming message of the
    /// running turn is collapsed; the head keeps the oldest messages verbatim.
    /// <para>
    /// The newest user message is not forced into the tail: a long tool-heavy run has its task
    /// message far behind the last turns, and extending the tail back to it would make the
    /// collapsed slice empty — the gate would then never compress the very run it was built for.
    /// The task itself is not lost: it is part of the summarizer input (only the last one-two
    /// messages are excluded from the prompt) and lands in the summary.
    /// </para>
    /// </summary>
    /// <returns>Messages to collapse; empty when there is nothing a summary could replace.</returns>
    internal static List<VisualChatMessage> PlanCompression(IReadOnlyList<VisualChatMessage> snapshot)
    {
        // Technical notes are not context (BuildWire drops them), so they must not occupy window
        // slots either: collapsing one into the summary would edit a line the model never saw.
        var visible = snapshot
            .Where(m => !m.IsTechnical && !ConversationSession.IsEmptyAssistant(m))
            .ToList();
        var total = visible.Count;

        // Head + tail already cover the whole history: a summary would collapse nothing.
        if (total < 4)
            return [];

        var windowSize = total < 6 ? 2 : 3;
        var tailStart = Math.Max(windowSize, total - windowSize);

        // Live messages of the run that triggered the compression are never collapsed: the agent
        // loop keeps streaming into these exact objects, so removing them from the history would
        // orphan the stream (the UI froze and the generation looked stopped).
        var firstLive = visible.FindIndex(m => m.IsStreaming);
        if (firstLive >= 0)
            tailStart = Math.Max(windowSize, Math.Min(tailStart, firstLive));

        // A single collapsed message is not worth an LLM call and would only add noise.
        if (tailStart - windowSize < 2)
            return [];

        return visible.GetRange(windowSize, tailStart - windowSize);
    }

    /// <summary>
    /// Replaces oversized tool results with a stub: the full text no longer fits the compressed
    /// context (the tool call itself stays, so the model still sees what was executed).
    /// </summary>
    private static void StubLongToolResults(IEnumerable<VisualChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.ToolCalls is not { Count: > 0 })
                continue;

            foreach (var toolCall in message.ToolCalls)
            {
                if (toolCall.Result is { Content.Length: > CompressedToolResultThreshold })
                {
                    toolCall.Result = new ToolResult
                    {
                        Content = $"The long result ({toolCall.Result.Content.Length} chars) was removed after compression.",
                        Name = toolCall.Function.Name,
                        DisplayName = toolCall.Result.DisplayName,
                        Success = toolCall.Result.Success,
                    };
                }
            }
        }
    }

    /// <summary>
    /// Rough context size of a request (~4 chars per token — the same heuristic the streaming
    /// estimate uses). Used to re-arm the compression threshold after a successful compression,
    /// where no provider usage number is available.
    /// <para>
    /// Tool schemas are part of every request, so they are counted too: a strict-mode toolset
    /// (JSON schema per tool, descriptions included) is several thousand tokens of constant
    /// overhead. Leaving it out made the re-armed value systematically lower than the
    /// provider-reported usage it replaces, so the next gate check diverged from reality.
    /// </para>
    /// </summary>
    private static int EstimateContextTokens(
        IEnumerable<VisualChatMessage> messages, IEnumerable<Tool>? tools = null)
    {
        var chars = 0;
        foreach (var message in messages)
        {
            // Same rule as BuildWire: a message the request does not carry is not context.
            if (message.IsTechnical)
                continue;

            chars += message.Content?.Length ?? 0;
            if (message.ToolCalls is not { Count: > 0 })
                continue;
            foreach (var toolCall in message.ToolCalls)
            {
                chars += toolCall.Function.Arguments?.Length ?? 0;
                chars += toolCall.Result?.Content?.Length ?? 0;
            }
        }

        if (tools is not null)
        {
            foreach (var tool in tools)
            {
                // Serialised the same way the request serialises it, so the estimate tracks the
                // real payload instead of a hand-maintained guess at the schema size.
                chars += JsonUtils.SerializeCompact(tool.NativeTool).Length;
            }
        }

        return chars / 4;
    }

    /// <summary>
    /// Сжатие контекста указанной сессии (например, ветки субагента).
    /// Состояние LLM-вызова записывается в <paramref name="resultCapture"/>.
    /// </summary>
    public IAsyncEnumerable<ChatDelta> CompressSessionAsync(
        ConversationSession targetSession,
        CompletionsResult resultCapture,
        CancellationToken cancellationToken)
        => CompressSessionCoreAsync(targetSession, resultCapture, cancellationToken);

    /// <summary>
    /// Сжатие контекста основной сессии: последние сообщения остаются дословно,
    /// история текст заменяется сводкой от LLM (resultCapture — состояние LLM-вызова).
    /// </summary>
    public IAsyncEnumerable<ChatDelta> CompressSessionAsync(
        CompletionsResult resultCapture,
        CancellationToken cancellationToken)
        => CompressSessionCoreAsync(Session, resultCapture, cancellationToken);

    /// <summary>
    /// Persists the current session and everything that changed since the last write.
    /// Callable at any time: it is a "persist now" flush on top of the autosave invariant,
    /// and it never throws (a failed write is logged and retried on the next flush).
    /// </summary>
    public async Task SaveSessionAsync()
    {
        ConversationSession[] pending;
        lock (_autosaveSync)
        {
            pending = [.. _dirtySessions, Session];
            _dirtySessions.Clear();
            _autosaveDebounce?.Cancel();
            _autosaveDebounce?.Dispose();
            _autosaveDebounce = null;
        }

        foreach (var session in pending.Distinct())
            await SaveSessionCoreAsync(session).ConfigureAwait(false);
    }

    private async Task SaveSessionCoreAsync(ConversationSession session)
    {
        await _saveGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(session.Id))
                return;

            await localStorage.SetItemAsync(session.Id, session).ConfigureAwait(false);
            UpdateSessionCache(session);
        }
        catch (Exception ex)
        {
            // Keep the session dirty so the next mutation or explicit flush retries.
            lock (_autosaveSync)
                _dirtySessions.Add(session);
            logger.LogError("Failed to save session {id}: {message}", session.Id, ex.Message);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void UpdateSessionCache(ConversationSession session)
    {
        if (_recentSessionsCache == null) return;

        var existing = _recentSessionsCache.FirstOrDefault(s => s.Id == session.Id);
        var preview = TruncatePreview(FirstUserMessage(session) ?? string.Empty);

        if (existing != null)
        {
            existing.FirstUserMessage = preview;
        }
        else
        {
            _recentSessionsCache.Add(new SessionSummary
            {
                Id = session.Id,
                CreatedAt = session.CreatedAt,
                FirstUserMessage = preview
            });
            _recentSessionsCache = [.. _recentSessionsCache.OrderByDescending(s => s.CreatedAt)];
        }
    }

    /// <summary>Content of the first user message, or null when the session has none.</summary>
    private static string? FirstUserMessage(ConversationSession session)
        => session.GetMessagesSnapshot().FirstOrDefault(m => m.Role == ChatMessageRole.User)?.Content;

    /// <summary>Truncates a session preview to 40 chars.</summary>
    private static string TruncatePreview(string content)
        => content.Length > 40 ? content[..40] + "..." : content;

    /// <summary>
    /// Streams exactly ONE LLM turn for the pinned session.
    /// <para>
    /// The request history is rebuilt from the session AT CALL TIME and validated by
    /// <see cref="ConversationSession.BuildWire"/> (orphan tool calls stubbed, empty assistant turns
    /// dropped, streaming messages excluded). This is the single invariant that makes every
    /// between-turn mutation reach the model: compression (CollapseMessages), message edit,
    /// message deletion, interject, mode switch — instead of only after the whole run ends.
    /// </para>
    /// <para>
    /// Tool execution does NOT happen here: the returned deltas carry tool calls outward
    /// (capture.AccumulatedToolCalls) and the caller runs the approval + execution path.
    /// </para>
    /// </summary>
    /// <param name="targetSession">Pinned run session.</param>
    /// <param name="live">Message the caller is about to write into; excluded from the request by reference.</param>
    /// <param name="tools">Explicit tool list (sub-agent branch); null resolves the enabled tools of the session's CURRENT mode.</param>
    /// <param name="resultCapture">Per-turn state accumulator (reset at entry).</param>
    public async IAsyncEnumerable<ChatDelta> GetTurnCompletionsAsync(
        ConversationSession targetSession,
        VisualChatMessage? live,
        IReadOnlyList<Tool>? tools,
        CompletionsResult resultCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        resultCapture.Reset();

        var systemPrompt = await systemPromptBuilder
            .PrepareSystemPromptAsync(targetSession.Mode, cancellationToken)
            .ConfigureAwait(false);

        var (messages, _) = targetSession.BuildWire(systemPrompt, contentFilter, live);

        // tools == null: resolve from the session's CURRENT mode, so a switch_mode executed by a
        // tool of the previous turn changes the tool set of THIS turn without extra plumbing.
        var resolved = tools ?? toolManager.GetEnabledTools(targetSession.Mode).ToList();

        // ONE LLM call per turn: the tool loop is deliberately OUT of this path. The caller owns
        // the turn cycle and executes tools between calls; running the loop here would perform
        // several hidden LLM rounds per "turn" and re-freeze the history we just rebuilt.
        await foreach (var delta in ChatGateway.GetCompletionsAsync(messages, resolved, targetSession, resultCapture, cancellationToken))
        {
            yield return delta;
        }
    }

    /// <summary>
    /// Streams ONE LLM turn for the CURRENT session (no live message, tools resolved from the
    /// session's mode). Thin convenience wrapper over
    /// <see cref="GetTurnCompletionsAsync"/>: the turn cycle still belongs to the caller.
    /// </summary>
    public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
        CompletionsResult resultCapture, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Pin the session: system prompt, history and tools belong to the session the run
        // started in (a run never spans two sessions - NewSession is blocked while it runs).
        await foreach (var chatDelta in GetTurnCompletionsAsync(
            Session, live: null, tools: null, resultCapture, cancellationToken))
        {
            yield return chatDelta;
        }
    }

    private const int _maxSessions = 5;
    private List<SessionSummary>? _recentSessionsCache;

    public async Task<List<SessionSummary>> GetRecentSessionsAsync(int count)
    {
        if (_recentSessionsCache != null)
            return [.. _recentSessionsCache.Take(count)];

        var sessionIds = await GetAllSessionIdsAsync();
        var summaries = new List<SessionSummary>();

        foreach (var id in sessionIds)
        {
            var session = await localStorage.TryGetItemAsync<ConversationSession>(id);
            var firstMessage = session is null ? null : FirstUserMessage(session);

            if (session == null || firstMessage == null)
            {
                await localStorage.RemoveItemAsync(id);
                logger.LogError("Invalid session {id} is removed", id);
                continue;
            }

            summaries.Add(new SessionSummary
            {
                Id = id,
                CreatedAt = session.CreatedAt,
                FirstUserMessage = TruncatePreview(firstMessage)
            });
        }

        _recentSessionsCache = [.. summaries.OrderByDescending(s => s.CreatedAt)];

        return [.. _recentSessionsCache.Take(count)];
    }

    public async Task NewSessionAsync()
    {
        // Flush the session being left (completed turns, tool results, mode) before the swap:
        // a swapped-out session must never lose work that autosave has not written yet.
        // A pending write counts as work even when the session has no messages (e.g. only the
        // mode changed) — the message count alone would silently drop that write.
        if (HasPendingWork(Session))
        {
            await SaveSessionAsync();
        }

        Session = CreateNewSession();

        await CleanupOldSessionsAsync();
    }

    private async Task CleanupOldSessionsAsync()
    {
        var recent = await GetRecentSessionsAsync(int.MaxValue);
        if (recent.Count > _maxSessions)
        {
            var sessionsToDelete = recent.Skip(_maxSessions).ToList();
            foreach (var sessionToDelete in sessionsToDelete)
            {
                await DeleteSessionAsync(sessionToDelete.Id);
            }
        }
    }

    /// <summary>Loads a session from storage and assigns its stored id, or null when not found.</summary>
    private async Task<ConversationSession?> TryLoadSessionAsync(string id)
    {
        var session = await localStorage.TryGetItemAsync<ConversationSession>(id);
        if (session == null)
            return null;

        session.Id = id;
        return session;
    }

    public async Task LoadSessionAsync(string id)
    {
        var loaded = await TryLoadSessionAsync(id);
        if (loaded is null)
            return;

        if (HasPendingWork(Session))
            await SaveSessionAsync();

        // Legacy cleanup at the load boundary: sessions written before the turn loop owned the
        // turn messages carry "empty turn" bubbles (eager onNewTurn placeholders). They must not
        // render and must not reach the model - new turns are created lazily, so nothing
        // recreates them.
        var purged = loaded.PurgeLegacyEmptyTurns();
        if (purged > 0)
        {
            logger.LogInformation("Removed {Count} legacy empty-turn message(s) from session {Id}.", purged, id);
            await SaveSessionCoreAsync(loaded);
        }

        Session = loaded;
    }

    /// <summary>
    /// True when the session has content worth flushing: messages or a change that autosave has
    /// not written yet (mode switch, compression rewrite, message removal).
    /// </summary>
    private bool HasPendingWork(ConversationSession? session)
    {
        if (session is null)
            return false;
        if (session.GetMessageCount() > 0)
            return true;

        lock (_autosaveSync)
            return _dirtySessions.Contains(session);
    }

    public async Task DeleteSessionAsync(string id)
    {
        if (Session?.Id == id)
        {
            DetachAutosave(Session);
            Session = CreateNewSession();
        }

        // A pending autosave must not resurrect the file we are deleting.
        lock (_autosaveSync)
            _dirtySessions.RemoveWhere(s => s.Id == id);

        await localStorage.RemoveItemAsync(id);

        _recentSessionsCache?.RemoveAll(s => s.Id == id);
    }

    private async Task<List<string>> GetAllSessionIdsAsync()
    {
        return [.. (await localStorage.GetAllKeysAsync()).Where(k => k.StartsWith("session_"))];
    }

    private static string GenerateSessionId() => $"session_{DateTime.Now:s}_{Guid.NewGuid():N}";

    private static ConversationSession CreateNewSession() => new() { Id = GenerateSessionId() };

    public async Task LoadLastSessionOrGenerateNewAsync()
    {
        // самая свежая сессия по времени создания, закодированному в id после префикса "session_"
        var lastSessionId = (await GetAllSessionIdsAsync())
            .OrderByDescending(ParseSessionCreatedAt)
            .FirstOrDefault();

        Session = (lastSessionId is not null ? await TryLoadSessionAsync(lastSessionId) : null) ?? CreateNewSession();
    }

    private static DateTime ParseSessionCreatedAt(string id)
    {
        // Id format: "session_{yyyy-MM-ddTHH:mm:ss}_{guid}". The timestamp is the leading 19
        // characters of the payload; the suffix only guarantees uniqueness (two sessions created
        // in the same second used to share an Id and silently overwrite each other in storage).
        var payload = id[8..];
        var stamp = payload.Length >= 19 ? payload[..19] : payload;
        return DateTime.TryParseExact(stamp, "s", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result
            : DateTime.MinValue;
    }

    public void Dispose()
    {
        Session.PropertyChanged -= SessionPropertyChanged;

        // Cannot await in a synchronous Dispose: drop the debounce timer and forget the
        // session. Callers that must not lose pending work call SaveSessionAsync first.
        lock (_autosaveSync)
        {
            _autosaveDebounce?.Cancel();
            _autosaveDebounce?.Dispose();
            _autosaveDebounce = null;
            _dirtySessions.Clear();
        }

        DetachAutosave(Session);
        // _saveGate is intentionally not disposed: an in-flight write may still release it.
    }
}
