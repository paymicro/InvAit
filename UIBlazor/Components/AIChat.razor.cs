using System.ComponentModel;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;
using UIBlazor.Components.Chat;
using ConversationSession = UIBlazor.Models.ConversationSession;

namespace UIBlazor.Components;

public partial class AiChat : RadzenComponent
{
    private bool IsLoading { get; set; }

    /// <summary>Windowed transcript of the main session (head / archive plaque / tail).</summary>
    private MessageListView? _messageList;

    private DotNetObjectReference<AiChat>? _dotNetRef;

    private CancellationTokenSource _cts = new();

    private bool _callSettings;

    /// <summary>
    /// True while the agent loop is actively running (ProcessStreamAsync in progress),
    /// false during compression and idle. Gates interjects: a user message sent during
    /// the stream is added to the session and picked up by the loop at the next turn
    /// boundary; during compression it is rejected (SetMessages would drop it).
    /// </summary>
    private bool _acceptInterjects;

    /// <summary>
    /// True while ANY compression runs, including the invisible ones (mid-run gate): the summary
    /// stream is swallowed by the loop, so without this flag the UI shows a silently frozen run.
    /// </summary>
    private bool _isCompressing;

    [Inject] private NotificationService NotificationService { get; set; } = null!;
    [Inject] private DialogService DialogService { get; set; } = null!;
    [Inject] private IChatService ChatService { get; set; } = null!;
    [Inject] private IToolManager ToolManager { get; set; } = null!;
    [Inject] private IProfileManager ProfileManager { get; set; } = null!;
    [Inject] private ICommonSettingsProvider CommonSettingsProvider { get; set; } = null!;
    [Inject] private IVsBridge VsBridge { get; set; } = null!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = null!;
    [Inject] private IMessageParser MessageParser { get; set; } = null!;
    [Inject] private ILogger<AiChat> Logger { get; set; } = null!;
    [Inject] private IRetryHandler RetryHandler { get; set; } = null!;
    [Inject] private IToolCallHandler ToolCallHandler { get; set; } = null!;
    [Inject] private ISubAgentExecutor SubAgentExecutor { get; set; } = null!;
    [Inject] private ISkillService SkillService { get; set; } = null!;
    [Inject] private UiErrorLogger UiErrorLogger { get; set; } = null!;

    public async Task NewSessionAsync()
    {
        // A run in flight pins its session; swapping mid-run would scatter turns across two
        // sessions. Autosave keeps the current one safe, so blocking here loses nothing.
        if (IsLoading)
            return;

        await ChatService.NewSessionAsync();
        await InvokeAsync(StateHasChanged);
    }

    public async Task SendMessageAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        // Во время сжатия interject отклоняется — компрессия перезаписывает историю из
        // снапшота и молча потеряла бы сообщение.
        if (IsLoading && !_acceptInterjects)
            return;

        var userMessage = VisualChatMessage.CreateUser(content, IsShortMessage(content));

        ChatService.Session.AddMessage(userMessage);
        await ScrollToBottomAsync();

        if (IsLoading)
        {
            // Interject во время стрима: сообщение уже добавлено в сессию сразу после
            // стримящегося сообщения; агентный цикл подхватит его на границе туров
            // (после завершения текущего tool-тура, до следующего LLM-вызова).
            await InvokeAsync(StateHasChanged);
            return;
        }

        await GetAiResponseAsync();
    }

    public async Task HandleCommandAsync(string command)
    {
        if (IsLoading) return;

        // The suggestion menu hands over the bare name ("compress"), while a hand-typed
        // "/compress" keeps its slash. Normalising here means the command is recognised either
        // way, and the dispatcher below compares against BuiltInCommand constants only.
        command = command.Trim().TrimStart('/');

        // Вызов скилла: "skill:{name}"
        if (command.StartsWith(AiChatInput.SkillCommandPrefix, StringComparison.Ordinal))
        {
            await InvokeSkillAsync(command[AiChatInput.SkillCommandPrefix.Length..]);
            return;
        }

        switch (command)
        {
            case BuiltInCommand.Compress:
                await CancelResponseAsync();
                _cts = new CancellationTokenSource();
                IsLoading = true;
                await InvokeAsync(StateHasChanged);
                try
                {
                    await CompressAsync(ChatService.Session, 0, _cts.Token);
                }
                finally
                {
                    IsLoading = false;
                    await InvokeAsync(StateHasChanged);
                }
                break;
        }
    }

    /// <summary>
    /// Активация скилла пользователем через слэш-команду.
    /// Загружает содержимое скилла и отправляет его модели как инструкцию.
    /// </summary>
    private async Task InvokeSkillAsync(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName)) return;

        VsToolResult result;
        try
        {
            var args = JsonUtils.SerializeCompact(new { skillName });
            result = await SkillService.LoadSkillContentMarkDownAsync(args, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load skill {SkillName}", skillName);
            NotificationService.NotifyError(UiErrorLogger, string.Format(SharedResource.CommandSkillNotFound, skillName), ex.Message, "Skill", ex);
            return;
        }

        if (!result.Success || string.IsNullOrWhiteSpace(result.Result))
        {
            NotificationService.NotifyError(UiErrorLogger, string.Format(SharedResource.CommandSkillNotFound, skillName), result.ErrorMessage, "Skill");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"The user activated the skill \"{skillName}\" via slash command. Follow its instructions below.");
        sb.AppendLine();
        sb.AppendLine(result.Result.Trim());

        await SendMessageAsync(sb.ToString());
    }

    private async Task ScrollToBottomAsync()
    {
        await Task.Yield();
        try
        {
            // Скролл — UI-операция и не должен жить на токене LLM-прогона: после отмены
            // ответа _cts остаётся cancelled до следующего GetAiResponseAsync (который сам
            // не стартует, если SendMessage упадёт раньше), и InvokeVoidAsync на этом токене
            // бросал OperationCanceledException в SendMessageAsync ДО старта нового прогона —
            // сообщения добавлялись визуально, но не отправлялись агенту и не сохранялись
            // в localStorage (до перезагрузки окна).
            await JsRuntime.InvokeVoidAsync("scrollToAnchor", CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Скролл — косметика: сбой JS-моста не должен ломать конвейер сообщений.
            Logger.LogWarning(ex, "ScrollToBottomAsync failed");
        }
    }

    /// <summary>
    /// Scrolls the chat to a specific message and highlights it. Used for the compression
    /// summary, which is inserted into the MIDDLE of the history while the user stares at the
    /// bottom of the chat.
    /// </summary>
    private async Task ScrollToMessageAsync(string messageId)
    {
        await Task.Yield();

        // The target may be inside the archive plaque: a message that is not in the DOM cannot be
        // scrolled to, so the window is expanded first (the compression summary lands in the middle
        // of the history - exactly the case this method exists for).
        if (_messageList is not null)
            await _messageList.EnsureVisibleAsync(messageId);

        // Let Blazor render the rewritten history first, otherwise the target node is missing.
        await InvokeAsync(StateHasChanged);
        await Task.Yield();

        try
        {
            await JsRuntime.InvokeVoidAsync("scrollToMessage", CancellationToken.None, messageId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ScrollToMessageAsync failed");
        }
    }

    /// <summary>Result content of a tool call that never produced one.</summary>
    private const string InterruptedToolResultContent =
        "The tool call was not executed (run interrupted before the result was produced).";

    private async Task GetAiResponseAsync()
    {
        await CancelResponseAsync();

        // ONE token source per RUN (not per turn): every turn and every retry of the run shares
        // it. The old code re-entered GetAiResponseAsync after each tool turn, which cancelled
        // its own token and created a new one - inside a turn loop that breaks approval and
        // compression cancellation (the re-entry cancelled the approval gate of the next turn).
        _cts = new CancellationTokenSource();
        await GetAiResponseInternalAsync(_cts.Token);
    }

    /// <summary>
    /// One agent run: pins the session, opens the interject window, runs the turn loop and
    /// guarantees the terminal state (IsLoading, closed messages, persisted session).
    /// </summary>
    private async Task GetAiResponseInternalAsync(CancellationToken runToken)
    {
        IsLoading = true;

        // Paint the loading state NOW, not on the first token. IsLoading is a plain auto-property
        // (no re-render on set), so without this the Cancel button and the busy state stayed
        // invisible until the first delta arrived - a run that had not produced a byte yet looked
        // like a frozen input with no way out.
        await InvokeAsync(StateHasChanged);

        // The run is PINNED to the session it started in: every turn message, tool result and
        // error goes there even if the current session reference changes underneath (autosave
        // persists by instance, so nothing is lost).
        var runSession = ChatService.Session;

        try
        {
            // Interject window: while the loop streams, user messages are appended to the
            // session and picked up at the END of the current turn (the next turn rebuilds the
            // request from the session). Closed during compression - that stream is swallowed.
            _acceptInterjects = true;
            await RunTurnsAsync(runSession, runToken);
        }
        finally
        {
            _acceptInterjects = false;

            // A turn may be abandoned mid-stream (error without retry budget, cancellation,
            // interrupted tool batch): close every message. A message left IsStreaming=true is
            // filtered out of the wire history forever and renders as a frozen bubble.
            foreach (var openMessage in runSession.GetMessagesSnapshot())
            {
                if (!openMessage.IsStreaming)
                    continue;
                openMessage.IsStreaming = false;
                openMessage.IsShouldRender = true;
            }

            // Terminal state of the run (success, error or cancellation) is always persisted:
            // this is the safety net on top of the autosave invariant (nothing may be lost
            // because a caller forgot to save). ONE write per run, not per turn.
            await ChatService.SaveSessionAsync();
            IsLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// The agent turn loop: ONE LLM call per iteration, tools executed between the calls, the
    /// request history rebuilt from the session at the start of EVERY turn (see
    /// <see cref="IChatService.GetTurnCompletionsAsync"/>). Compression, message edits,
    /// deletions, interjects and mode switches applied between turns therefore reach the model
    /// without any extra sync plumbing.
    /// </summary>
    private async Task RunTurnsAsync(ConversationSession runSession, CancellationToken runToken)
    {
        // Run-level state (FinishReason / Error / iteration cap), reported ONCE per run.
        var lastCapture = new CompletionsResult();

        try
        {
            for (var iteration = 0; ; iteration++)
            {
                if (runToken.IsCancellationRequested)
                    return;

                // Compression gate: checked before EVERY turn (the first check covers the
                // start-of-run threshold too). The summary stream is swallowed here; the user
                // sees the badge + the notification raised by the service events.
                if (ChatService.NeedsCompression(runSession))
                {
                    var compressed = await CompressAsync(runSession, 0, runToken);
                    if (!compressed || runToken.IsCancellationRequested)
                        return;
                }

                // Interject baseline for THIS turn: message identity (Revision as a cheap gate),
                // never a numeric index - compression rewrites the history and shifts every
                // position, an index would re-read old turns or miss the new one.
                var revisionBeforeTurn = runSession.Revision;
                var userIdsBeforeTurn = UserMessageIds(runSession);

                // Per-turn state: a FRESH capture and FRESH callbacks per turn (a stale closure
                // would keep writing the previous turn's capture - message.Model got lost).
                var capture = new CompletionsResult();
                lastCapture = capture;

                var message = await RunSingleTurnAsync(runSession, capture, runToken);
                if (message is null)
                    return; // cancelled, or the retry budget of this turn is exhausted

                // The user cancelled between the turn and the batch: nothing may execute now
                // (PrepareToolsForApprovals would re-register waiters of an aborted run).
                if (runToken.IsCancellationRequested)
                    return;

                var calls = capture.AccumulatedToolCalls;
                if (calls is not { Count: > 0 })
                {
                    // A plain text turn ends the run - unless the user interjected while it
                    // streamed: the new user message must be answered by another turn.
                    if (!HasNewUserMessage(runSession, revisionBeforeTurn, userIdsBeforeTurn))
                        return;
                    continue;
                }

                if (!await ExecuteToolBatchAsync(message, calls, runSession, runToken))
                    return; // cancelled / interrupted mid-batch: no further LLM call
            }
        }
        finally
        {
            NotifyIfNeeded(lastCapture);
        }
    }

    /// <summary>
    /// Streams exactly ONE LLM turn into its own message and returns that message, or
    /// <c>null</c> when the run must stop (cancelled, or the retry budget is exhausted).
    /// <para>
    /// The turn message is created LAZILY: the object exists from the start (it is the stream
    /// target and the message excluded from its own request by reference), and it enters the
    /// session IMMEDIATELY, before the first token. The bubble is the user's feedback that the
    /// turn started: without it the input froze with no indicator and no Cancel button for as long
    /// as the provider took to send the first byte. It still cannot pollute the history: a turn that
    /// ends without payload is removed from the session again, and while it streams it is excluded
    /// from the wire by <c>IsStreaming</c> and by being the <c>live</c> message of its own request.
    /// </para>
    /// <para>
    /// Retries are scoped to THIS turn: <see cref="HandleErrorAsync"/> owns the countdown UI and
    /// the budget, and after it allowed a retry the loop below re-enters the very same request.
    /// It never recurses into the run loop, so a retried turn neither starts a second run nor
    /// cancels its own token.
    /// </para>
    /// </summary>
    private async Task<VisualChatMessage?> RunSingleTurnAsync(
        ConversationSession runSession, CompletionsResult capture, CancellationToken runToken)
    {
        for (var retryCount = 0; ;)
        {
            var turnMessage = VisualChatMessage.CreateStreaming();

            // The bubble exists from the first millisecond. Eager, not on-first-token: the run
            // looks hung otherwise - no placeholder, no streaming indicator and no Cancel button
            // until the provider's first byte (which can take seconds). The empty turn is still
            // impossible to persist: no payload means it is removed again at the end of this method.
            runSession.AddMessage(turnMessage);
            await InvokeAsync(StateHasChanged);

            // A payload repaints THIS message only. Going through the parent re-rendered the
            // whole transcript on every token (SetParameters + ShouldRender over every message in
            // the session); with a long history that dominated the frame budget. The bubble
            // subscribes to the message itself, so the cost per token is one component.
            // Parent-level renders stay at structural moments (message added/removed, run end).
            void Refresh() => turnMessage.NotifyChanged();

            try
            {
                await ChatService.ProcessStreamAsync(
                    turnMessage,
                    ChatService.GetTurnCompletionsAsync(runSession, turnMessage, null, capture, runToken),
                    onContentUpdate: content =>
                    {
                        MessageParser.UpdateSegments(content, turnMessage);
                        Refresh();
                    },
                    onToolCallsUpdate: toolCalls =>
                    {
                        turnMessage.ToolCalls = toolCalls;
                        turnMessage.IsShouldRender = true;
                        Refresh();
                    },
                    onStateChange: () =>
                    {
                        turnMessage.Model ??= capture.Model;
                        Refresh();
                    },
                    capture,
                    runToken);

                // The badge (Tokens/ReasoningTokens/Timings) is stamped by ProcessStreamAsync
                // against THIS turn's capture: timings are per-turn, never per-run.
                turnMessage.Model ??= capture.Model;

                if (!HasPayload(turnMessage))
                {
                    // Nothing arrived: an empty assistant bubble is noise, and an empty assistant
                    // turn is rejected by some providers. Drop it - the run itself continues.
                    runSession.RemoveMessage(turnMessage.Id);
                    await InvokeAsync(StateHasChanged);
                    return null;
                }

                ParsePlan(turnMessage);
                turnMessage.IsShouldRender = true;
                await InvokeAsync(StateHasChanged);
                return turnMessage;
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                // Payload is read BEFORE HandleCancellation: the handler writes
                // "Cancelled by user..." into an empty message, so asking afterwards always says
                // "non-empty" and a bubble that carries nothing survives the cancel.
                if (!HasPayload(turnMessage))
                {
                    // Cancelled before the first token: nothing arrived, so nothing stays. The user
                    // knows they cancelled - a bubble reporting it is noise, and an assistant turn
                    // with no text is unusable context for the next request.
                    runSession.RemoveMessage(turnMessage.Id);
                    await InvokeAsync(StateHasChanged);
                    return null;
                }

                // A partial answer or an open tool batch must stay: the tool calls need results in
                // the wire history, and the text the model already produced is worth keeping.
                HandleCancellation(turnMessage);
                return turnMessage;
            }
            catch (Exception ex)
            {
                // The failure must be visible: the bubble is already in the session and carries
                // the error text after HandleErrorAsync.
                if (!await HandleErrorAsync(turnMessage, ex, ++retryCount, runSession, runToken))
                    return null;

                // HandleErrorAsync removed the partial turn from the session: retry the SAME
                // turn with a FRESH message (reusing the object would duplicate its content).
            }
        }
    }

    /// <summary>
    /// Executes the tool calls of ONE turn through the existing approval/execution path.
    /// <para>
    /// Approvals are prepared EXACTLY ONCE for the whole batch: the method cancels every pending
    /// gate and re-registers the ask_user waiters first, so a second call would drop decisions
    /// the user already made. ProcessToolCallsAsync then runs the batch.
    /// </para>
    /// <para>
    /// A <c>switch_mode</c> call inside the batch changes the mode for the REST of the batch
    /// (the handler receives ONE mode per call), so the batch is split at the switch and the
    /// remainder runs under the NEW mode: a call the new mode forbids is rejected by the
    /// handler with a stub result instead of being executed.
    /// </para>
    /// </summary>
    /// <returns>True when the batch completed; false when the run was cancelled or failed.</returns>
    private async Task<bool> ExecuteToolBatchAsync(VisualChatMessage message, List<ToolCall> calls,
        ConversationSession runSession, CancellationToken runToken)
    {
        // Same objects: the results render live on this turn message.
        message.ToolCalls = calls;
        message.IsShouldRender = true;

        ToolCallHandler.PrepareToolsForApprovals(calls);
        await InvokeAsync(StateHasChanged);

        try
        {
            var start = 0;
            while (start < calls.Count)
            {
                runToken.ThrowIfCancellationRequested();

                // Up to and INCLUDING the next switch_mode call (or the whole remainder):
                // the mode change must be visible to the handler of the following slice.
                var switchIndex = calls.FindIndex(start, call => call.Function.Name == BasicEnum.SwitchMode);
                var end = switchIndex < 0 ? calls.Count : switchIndex + 1;

                await ToolCallHandler.ProcessToolCallsAsync(calls.GetRange(start, end - start), runSession.Mode, runToken);
                start = end;
            }
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Tool batch execution failed");
            NotificationService.NotifyError(UiErrorLogger, "Tool execution failed", ex.Message, "Tools", ex);
            return false;
        }
        finally
        {
            // Whatever interrupted the batch, EVERY call needs a Result - an
            // assistant(tool_calls) turn without matching tool messages is a hard API 400 on
            // the next turn.
            EnsureToolResults(calls);
            message.IsShouldRender = true;
            await InvokeAsync(StateHasChanged);
        }

        return !runToken.IsCancellationRequested;
    }

    /// <summary>
    /// True when the session carries payload a turn could stream into the UI. Used to decide
    /// whether a turn message enters the session at all.
    /// </summary>
    private static bool HasPayload(VisualChatMessage message)
        => !string.IsNullOrEmpty(message.Content)
           || !string.IsNullOrEmpty(message.ReasoningContent)
           || message.ToolCalls is { Count: > 0 };

    /// <summary>Ids of the user messages of the session (interject baseline).</summary>
    private static HashSet<string> UserMessageIds(ConversationSession session)
        => session.GetMessagesSnapshot()
            .Where(m => m.Role == ChatMessageRole.User)
            .Select(m => m.Id)
            .ToHashSet();

    /// <summary>
    /// True when the user appended a message while the turn streamed. Detected by message
    /// identity (Revision only as a cheap "something changed" gate), never by a numeric index:
    /// compression rewrites the history and shifts every position.
    /// </summary>
    private static bool HasNewUserMessage(ConversationSession session, long revisionBeforeTurn, HashSet<string> userIdsBeforeTurn)
    {
        if (session.Revision == revisionBeforeTurn)
            return false;

        return session.GetMessagesSnapshot()
            .Any(m => m.Role == ChatMessageRole.User && !userIdsBeforeTurn.Contains(m.Id));
    }

    /// <summary>
    /// Guarantees a Result for EVERY call of a batch: a batch interrupted by cancellation, an
    /// exception or an unresolved approval must not leave a tool call without an answer.
    /// </summary>
    private void EnsureToolResults(IEnumerable<ToolCall>? calls)
    {
        if (calls is null)
            return;

        foreach (var toolCall in calls)
        {
            if (toolCall.Result is not null)
                continue;

            toolCall.ApprovalStatus = ToolApprovalStatus.Rejected;
            toolCall.Result = new ToolResult
            {
                Success = false,
                Content = InterruptedToolResultContent,
                Name = toolCall.Function.Name,
                DisplayName = ToolResult.GetDisplayName(false,
                    ToolManager.GetTool(toolCall.Function.Name)?.DisplayName ?? toolCall.Function.Name)
            };
        }
    }

    /// <summary>
    /// Сжатие сессии. Полностью прозрачный для пользователя процесс.
    /// Выглядит как еще один промежуточный запрос и реорганизация сообщений.
    /// </summary>
    /// <remarks>
    /// The session is passed in explicitly: a run compresses the session it is PINNED to, not
    /// whatever <see cref="IChatService.Session"/> currently points at. The summary stream is
    /// swallowed here - the user only sees the compression badge + notification.
    /// </remarks>
    /// <param name="retryCount">Количество повторов</param>
    /// <returns>Сжалась ли сессия. False если завершилось ошибкой или отменой</returns>
    private async Task<bool> CompressAsync(ConversationSession session, int retryCount, CancellationToken cancellationToken)
    {
        _isCompressing = true;
        var assistantMessage = VisualChatMessage.CreateStreaming("## ♻ \n\n");
        MessageParser.UpdateSegments(assistantMessage.Content, assistantMessage);
        session.AddMessage(assistantMessage);
        await InvokeAsync(StateHasChanged);

        var completions = new CompletionsResult();

        try
        {
            await ChatService.ProcessStreamAsync(
                 assistantMessage,
                 ChatService.CompressSessionAsync(session, completions, cancellationToken),
                 onContentUpdate: content => MessageParser.UpdateSegments(content, assistantMessage),
                 onToolCallsUpdate: toolCalls =>
                 {
                     assistantMessage.ToolCalls = toolCalls;
                     assistantMessage.IsShouldRender = true;
                 },
                 onStateChange: () =>
                 {
                     assistantMessage.Model ??= completions.Model;
                     InvokeAsync(StateHasChanged);
                 },
                 completions,
                 cancellationToken);

            // Сегменты больше не достраиваются пачкой по всей истории: транскрипт окнирован, каждое
            // сообщение готовится лениво при первой материализации (MessageListView ->
            // MessagePresentation.Prepare). Полный проход здесь был ещё одним блокирующим циклом
            // на WASM-потоке, а fallback-рендер по Content всё равно снимает риск пустого пузыря.

            // Компрессия вставляет саммари на место сжатой середины истории; служебный
            // пузырь «♻», в который стримилось саммари, больше не нужен — иначе в конце чата
            // остался бы второй (невидимый) экземпляр сводки. При ошибке/отмене пузырь
            // остаётся: в нём текст ошибки.
            //
            // НЕ прокручиваем к сводке: она теперь в СЕРЕДИНЕ истории, а пользователь смотрит
            // в конец чата (там идёт работа). Насильный скролл вверх по длинной истории сбивает
            // с толку; раскрытие — по клику на уведомление (OnCompressionNotificationClick).
            session.RemoveMessage(assistantMessage.Id);
            await ChatService.SaveSessionAsync();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            HandleCancellation(assistantMessage);
            return false;
        }
        catch (Exception ex)
        {
            // Retry the COMPRESSION (not the run): HandleErrorAsync removed the partial bubble.
            if (!await HandleErrorAsync(assistantMessage, ex, ++retryCount, session, cancellationToken))
                return false;

            return await CompressAsync(session, retryCount, cancellationToken);
        }
        finally
        {
            _isCompressing = false;
            assistantMessage.IsStreaming = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void NotifyIfNeeded(CompletionsResult result)
    {
        if (result.FinishReason?.Equals("length", StringComparison.OrdinalIgnoreCase) == true)
        {
            NotificationService.NotifyError(UiErrorLogger, SharedResource.ErrorFinishByLength, null, "Completions", duration: 30_000);
        }

        if (!string.IsNullOrEmpty(result.Error))
        {
            NotificationService.NotifyError(UiErrorLogger, result.Error, null, "Completions", duration: 30_000);
        }
    }

    private void HandleCancellation(VisualChatMessage message)
    {
        if (string.IsNullOrEmpty(message.Content))
        {
            message.Content = "Cancelled by user...";
            MessageParser.UpdateSegments(message.Content, message);
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var toolCall in message.ToolCalls)
            {
                toolCall.ApprovalStatus = ToolApprovalStatus.Rejected;
                toolCall.Result ??= new ToolResult
                {
                    Success = false,
                    // Never an EMPTY tool message: it is part of the wire history of the next
                    // turn and some providers reject empty tool content.
                    Content = InterruptedToolResultContent,
                    Name = toolCall.Function.Name,
                    DisplayName = "❌ Cancelled"
                };
            }
        }
    }

    /// <summary>
    /// Owns the UI retry protocol of ONE turn: the notification, the retry badge with its
    /// countdown, and the removal of the partial turn so the next attempt starts from a clean
    /// history.
    /// </summary>
    /// <returns>
    /// True when the caller should re-enter the SAME turn; false when the failure is terminal
    /// (retry budget exhausted, or the user cancelled during the countdown).
    /// </returns>
    private async Task<bool> HandleErrorAsync(VisualChatMessage message, Exception ex,
        int retryCount, ConversationSession session, CancellationToken cancellationToken)
    {
        var maxRetries = CommonSettingsProvider.Current.MaxRetries;
        Logger.LogError(ex, "Getting response error");

        // A failure that cannot succeed on a repeat must not spend the budget. Without this the
        // user waited ~19 minutes (10 attempts, 2/5/10/20s backoff) to be told about a wrong API
        // key or a model that does not exist - answers that were knowable immediately.
        // A non-LlmApiException is our own bug, not a transport hiccup: fail loudly at once.
        var retryable = ex is LlmApiException { IsRetryable: true };

        // The counter is only meaningful when the budget is actually being spent: a permanent
        // failure would otherwise announce "[1/10]" while no second attempt will ever happen.
        NotificationService.NotifyError(
            UiErrorLogger,
            retryable ? $"[{retryCount}/{maxRetries}] Response error" : "Response error",
            ex.Message, "HTTP transport", ex, duration: 25_000);

        if (!retryable && retryCount < maxRetries)
        {
            Logger.LogWarning(ex, "Permanent failure, not retried (would fail identically).");
        }

        if (!retryable || retryCount >= maxRetries)
        {
            // Финал: перезаписываем контент текстом ошибки. При ретраебельной ошибке контент
            // НЕ трогаем — сообщение будет удалено перед retry, перезапись + перестройка
            // сегментов здесь была бы мёртвой работой (и затирала бы частичный ответ модели).
            // The attempt counter is only meaningful when the budget was actually spent; a
            // permanent failure reports the reason alone ("[1/10]" would claim nine attempts).
            message.Content = retryable
                ? $"Error: {ex.Message} [{retryCount}/{maxRetries}]"
                : $"Error: {ex.Message}";
            message.Segments.Clear();
            MessageParser.UpdateSegments(message.Content, message);
            // Tool calls of the abandoned turn still need an answer for the wire history.
            EnsureToolResults(message.ToolCalls);
            // The error text is part of the history: persist it now, not only on the next run.
            await ChatService.SaveSessionAsync();
            return false;
        }

        var delay = RetryHandler.GetRetryDelay(retryCount);

        message.MaxRetryAttempts = maxRetries;
        message.RetryAttempt = retryCount;

        try
        {
            await RetryHandler.WaitForRetryAsync(delay, i =>
            {
                message.RetryCountdown = i;
                InvokeAsync(StateHasChanged);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            message.RetryCountdown = 0;
        }

        // Only the failed turn is dropped: completed turns keep their content and tool results,
        // so the retried turn sees a consistent history instead of a wiped chain.
        session.RemoveMessage(message.Id);

        // ...but the failed attempt leaves a trace. The partial turn is gone, so without this the
        // transcript would show retries only in the log — and only the LAST failure would ever be
        // visible, as the error text of the terminal turn. The note is added AFTER the removal so
        // it lands where the failed turn was, and it is added AFTER the countdown so a run the
        // user cancels mid-wait leaves no "retrying" line behind.
        //
        // The line carries only the attempt numbers and the reason: the word "Retry" is the
        // rendered TITLE of the note (SharedResource.RetryNoteTitle), so the title exists once in
        // the UI and the persisted content stays comparable across attempts.
        session.AddMessage(VisualChatMessage.CreateTechnical(
            $"{string.Format(SharedResource.RetryNoteFormat, retryCount, maxRetries, delay)} — {DescribeError(ex)}"));

        return true;
    }

    /// <summary>
    /// Longest error text kept in a technical note. Provider error bodies are sometimes a whole
    /// HTML page; the note is a one-line trace, not a dump (the full text stays in the log and in
    /// the terminal error message).
    /// </summary>
    private const int TechnicalNoteMaxLength = 200;

    /// <summary>
    /// One-line description of a failure for a technical note: the first line, truncated.
    /// </summary>
    private static string DescribeError(Exception ex)
    {
        var text = ex.Message.ReplaceLineEndings(" ").Trim();
        if (text.Length == 0)
            text = ex.GetType().Name;
        return text.Length <= TechnicalNoteMaxLength
            ? text
            : text[..TechnicalNoteMaxLength] + "…";
    }


    private static void ParsePlan(VisualChatMessage message)
        => MessagePresentation.ApplyPlan(message);

    private async Task ExecutePlanAsync(VisualChatMessage message)
    {
        if (!message.HasPlan) return;

        ChatService.Session.Mode = AppMode.Agent;
        await SendMessageAsync("Implement the plan.");
    }

    private Task HandleToolApprovalAsync((string MessageId, string SegmentId, bool Approved) args)
    {
        return ToolCallHandler.HandleApprovalAsync(args.SegmentId, args.Approved);
    }

    /// <summary>Short messages open expanded, long ones start collapsed behind "Show more".</summary>
    private static bool IsShortMessage(string content) => MessagePresentation.IsShortMessage(content);

    /// <summary>
    /// Session transcript, cached by IDENTITY + VERSION: the list is rebuilt only when the session
    /// object or <see cref="ConversationSession.Revision"/> changes. A fresh list on every render
    /// (the old behaviour) meant a copy of the whole history per frame - measurable at 500 messages,
    /// and it also made the windowed view look like a different list on every pass.
    /// <para>
    /// The key is the revision, NOT the message COUNT: a count cannot tell a replacement from a
    /// no-op. Regenerate is exactly that shape - the last assistant message is removed and the new
    /// turn message is added in the SAME event-handler batch - so one render sees "N messages"
    /// before and after, the cache answers with the pre-change list, and the transcript keeps
    /// showing the deleted answer while the new one streams into a bubble that is not in the DOM.
    /// The list only caught up on the next structural change (the "+1 message" the user saw).
    /// </para>
    /// </summary>
    private IReadOnlyList<VisualChatMessage> GetMessagesSnapshot()
    {
        var session = ChatService.Session;
        var revision = session.Revision;

        if (_messagesSnapshot is null
            || !ReferenceEquals(_messagesSnapshotSession, session)
            || _messagesSnapshotRevision != revision)
        {
            _messagesSnapshotSession = session;
            _messagesSnapshotRevision = revision;
            _messagesSnapshot = session.GetMessagesSnapshot();
        }

        return _messagesSnapshot;
    }

    private IReadOnlyList<VisualChatMessage>? _messagesSnapshot;
    private ConversationSession? _messagesSnapshotSession;

    /// <summary><see cref="ConversationSession.Revision"/> the cached snapshot was taken at.</summary>
    private long _messagesSnapshotRevision = -1;

    /// <summary>
    /// Manual compression from the archive plaque. Reuses the existing /compress path: the plaque is
    /// where the context cost is visible, so the action that reduces it belongs next to the number.
    /// </summary>
    private async Task CompressFromPlaqueAsync()
    {
        await HandleCommandAsync(BuiltInCommand.Compress);
    }

    private async Task CancelResponseAsync() => await _cts.CancelAsync();

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        _dotNetRef = DotNetObjectReference.Create(this);
        ChatService.SessionChanged += HandleSessionChanged;
        ChatService.CompressionStarted += OnCompressionStarted;
        ChatService.CompressionCompleted += OnCompressionCompleted;
        SubAgentExecutor.SubAgentStateChanged += OnSubAgentStateChanged;
        ToolCallHandler.ApprovalRequired += OnApprovalRequired;

        ToolManager.RegisterAllTools();
        await VsBridge.InitializeAsync();
        await InvokeAsync(StateHasChanged);
    }

    private void HandleSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConversationSession))
            return;

        // No history walk here anymore: the transcript is windowed and every message is prepared
        // lazily on its first materialisation (MessageListView -> MessagePresentation). A full pass
        // over 500 messages (plan regex + segment building) on this path was one of the blocks that
        // made the WebView2 watchdog kill the renderer.
        _messagesSnapshot = null;
        InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Any compression (manual /compress, mid-run gate) makes the chat look frozen: the summary
    /// stream is consumed by the service, so nothing arrives in the UI. Show the busy badge.
    /// Sub-agent branches are filtered out — their own view has an indicator.
    /// </summary>
    private void OnCompressionStarted(ConversationSession session)
    {
        if (!ReferenceEquals(session, ChatService.Session))
            return;

        _isCompressing = true;
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// The history was rewritten and the summary sits in the MIDDLE of the chat, while the user
    /// keeps looking at the bottom (a mid-run compression happens while the run is still
    /// streaming). No forced scroll — that would yank the view away from the live output.
    /// The notification tells what happened and reveals the summary on click.
    /// </summary>
    private void OnCompressionCompleted(CompressionReport report)
    {
        if (!ReferenceEquals(report.Session, ChatService.Session))
            return;

        _isCompressing = false;
        _ = InvokeAsync(() =>
        {
            NotificationService.Notify(
                NotificationSeverity.Info,
                SharedResource.ContextCompressed,
                string.Format(SharedResource.ContextCompressedDetail, report.CollapsedCount),
                8_000,
                OnCompressionNotificationClick,
                closeOnClick: false,
                payload: report.Summary.Id);
            StateHasChanged();
        });
    }

    /// <summary>
    /// Click-to-reveal: scrolls to the summary ONLY when the user asks for it.
    /// </summary>
    private void OnCompressionNotificationClick(NotificationMessage message)
    {
        if (message.Payload is string summaryId)
            _ = ScrollToMessageAsync(summaryId);
    }

    /// <summary>
    /// Called when a sub-agent's state changes during execution.
    /// Triggers Blazor re-render so the ToolCallBlock component can pick up
    /// the newly-attached SubAgentMessage, subscribe to its StateChanged event,
    /// and render the SubAgentView with live updates.
    /// Also checks if the sub-agent has a pending tool call requiring user action.
    /// </summary>
    private void OnSubAgentStateChanged(SubAgentMessage subAgent)
    {
        // Check if sub-agent has a tool call requiring user action
        if (!string.IsNullOrEmpty(subAgent.PendingToolCallId))
        {
            var toolCallId = subAgent.PendingToolCallId;
            subAgent.PendingToolCallId = null; // Clear to avoid duplicate notifications
            _ = InvokeAsync(async () =>
            {
                try
                {
                    await HandleApprovalRequiredAsync(toolCallId, isSubAgent: true);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error handling sub-agent approval notification");
                }
            });
        }

        InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Called when the main agent's ToolCallHandler has a tool requiring approval or ask_user.
    /// </summary>
    private void OnApprovalRequired(string toolCallId)
    {
        _ = InvokeAsync(async () =>
        {
            try
            {
                await HandleApprovalRequiredAsync(toolCallId, isSubAgent: false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error handling approval notification");
            }
        });
    }

    /// <summary>
    /// Handles a tool call that requires user action (approval or ask_user).
    /// Shows a notification and scrolls to the tool call element with highlight.
    /// </summary>
    private async Task HandleApprovalRequiredAsync(string toolCallId, bool isSubAgent)
    {
        // Show notification
        NotificationService.NotifyWarning(UiErrorLogger, isSubAgent
            ? $"{SharedResource.SubAgent}: {SharedResource.ApproveRequired}"
            : SharedResource.ApproveRequired, null, "Approval");

        // Wait for Blazor to render the tool call block, then scroll to it
        await Task.Yield();
        await InvokeAsync(StateHasChanged);
        await Task.Yield();
        await JsRuntime.InvokeVoidAsync("scrollToToolCall", toolCallId);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await JsRuntime.InvokeVoidAsync("setChatHandler", _dotNetRef);
        }
    }

    private async Task OnProfileChangeAsync(object value)
    {
        var profileId = value as string;
        if (!string.IsNullOrEmpty(profileId))
        {
            await ProfileManager.ActivateProfileAsync(profileId);
            NotificationService.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Info,
                Summary = "Profile Changed",
                Detail = "Active profile updated.",
                Duration = 1000,
                ShowProgress = true,
            });
        }
    }

    private static void OnEditMessage(VisualChatMessage message)
    {
        message.TempContent = message.Content;
        message.IsEditing = true;
    }

    private static void OnCancelEdit(VisualChatMessage message)
    {
        message.IsEditing = false;
        message.TempContent = string.Empty;
    }

    private async Task OnSaveEditAsync(VisualChatMessage message)
    {
        message.Content = message.TempContent;
        message.Segments.Clear();
        MessageParser.UpdateSegments(message.Content, message);
        message.IsEditing = false;

        ParsePlan(message);

        ChatService.Session.UpdateMessage(message.Id, message.Content);
        await ChatService.SaveSessionAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnDeleteMessageAsync(VisualChatMessage message)
    {
        ChatService.Session.RemoveMessage(message.Id);
        await ChatService.SaveSessionAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnRegenerateLastAsync()
    {
        var lastAssistantMessage = ChatService.Session.GetLastOrDefaultMessage(m => m.Role == ChatMessageRole.Assistant);
        if (lastAssistantMessage != null)
        {
            ChatService.Session.RemoveMessage(lastAssistantMessage.Id);
            await GetAiResponseAsync();
        }
    }

    private async Task OnShowSettingsAsync()
    {
        _callSettings = true; // для отрисовки анимации кнопки IsBusy
        await InvokeAsync(StateHasChanged);
        await Task.Yield();

        await DialogService.OpenSideAsync<SettingsDialog>(SharedResource.Settings,
            options: new SideDialogOptions
            {
                CloseDialogOnOverlayClick = true,
                Resizable = true,
                Position = DialogPosition.Right,
                MinHeight = 250.0,
                MinWidth = 400.0
            });

        _callSettings = false;
    }

    public override void Dispose()
    {
        base.Dispose();

        _dotNetRef?.Dispose();
        ChatService.SessionChanged -= HandleSessionChanged;
        ChatService.CompressionStarted -= OnCompressionStarted;
        ChatService.CompressionCompleted -= OnCompressionCompleted;
        SubAgentExecutor.SubAgentStateChanged -= OnSubAgentStateChanged;
        ToolCallHandler.ApprovalRequired -= OnApprovalRequired;

        _cts?.Cancel();
        _cts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
