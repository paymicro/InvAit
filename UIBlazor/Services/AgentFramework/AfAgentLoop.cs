using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// The MAIN agent's LLM gateway: a Microsoft.Extensions.AI <see cref="FunctionInvokingChatClient"/>
/// run that carries the model ↔ tool round-trips in the chat-client pipeline
/// (MaximumIterationsPerRequest caps sub-agent runs). Tool execution routes back through
/// <see cref="IToolCallHandler"/>, so the approval flow (category rules, ask_user, parallel
/// delegate_task, ApprovalRequired UI events) stays identical to the legacy loop. App-level
/// features (todo / mode / skills / web / file memory / compaction) stay ours - the
/// harness-provided variants were never wired in. Sub-agent branches (AfSubAgentOrchestrator)
/// reuse this loop with MaximumIterations set; the single-shot adapter
/// (<see cref="AfCompletionsAdapter"/>) remains for context compression.
/// </summary>
internal sealed class AfAgentLoop(
    Func<ConnectionProfile> profile,
    HttpClient httpClient,
    IToolCallHandler toolCallHandler,
    Func<ConnectionProfile, HttpClient, IChatClient>? chatClientFactory = null) : ICompletionsGateway
{
    private readonly Func<ConnectionProfile, HttpClient, IChatClient> _chatClientFactory =
        chatClientFactory ?? ((p, h) => AfChatClientFactory.Create(p, h));

    /// <summary>
    /// Function-invocation iteration cap for this run (MaximumIterationsPerRequest).
    /// Null = stack default (40). Sub-agent loops set it from profile.MaxIterationsPerSubAgent;
    /// the main loop stays on the default.
    /// </summary>
    public int? MaximumIterations { get; init; }

    /// <summary>
    /// Optional pause gate checked at every iteration boundary (before each LLM call
    /// and before each tool execution). Sub-agent loops pass the branch's
    /// WaitIfPausedAsync so a user-paused branch stops making progress immediately,
    /// not only between ProcessStreamAsync calls. Cancellation propagates normally.
    /// </summary>
    public Func<CancellationToken, Task>? PauseGate { get; init; }

    public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
        IReadOnlyList<WireMessage> messages,
        IEnumerable<Tool>? tools,
        ConversationSession targetSession,
        CompletionsResult resultCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var currentProfile = profile();
        // AfMessageMapper.Map возвращает List — добавляем assistant/tool сообщения между итерациями напрямую
        var chatMessages = AfMessageMapper.Map(messages);
        // Базовая линия сканирования interject-сообщений: всё, что уже в сессии, отображено в
        // chatMessages (история из снапшота); всё, что добавится после — новые user-сообщения
        // (вмешательства пользователя в работающий агент), подхватываются на границе туров.
        var consumedSessionMessages = targetSession.GetMessageCount();
        // Объявления only — БЕЗ делегата исполнения (см. правку 2)
        var chatOptions = AfChatClientFactory.BuildChatOptions(
            currentProfile,
            AfToolMapper.Map(tools ?? []));

        using var client = _chatClientFactory(currentProfile, httpClient);
        var maxIterations = MaximumIterations ?? 40; // legacy FICC default

        // Interject-сообщения текущего тура (заполняется на границе туров сканом сессии)
        List<string> interjects = [];

        for (var iteration = 0; ; iteration++)
        {
            // Pause gate: a user-paused branch blocks here before the next LLM call.
            if (PauseGate is not null)
                await PauseGate(cancellationToken);

            var text = new StringBuilder();

            var updates = AfCancelRace.GuardAsync(
                client.GetStreamingResponseAsync(chatMessages, chatOptions, cancellationToken),
                () => "Request timed out",
                cancellationToken);

            await foreach (var delta in AfDeltaMapper.PumpAsync(updates, targetSession, resultCapture, cancellationToken))
            {
                if (delta.Content is not null)
                    text.Append(delta.Content);
                yield return delta; // прогрессивный UI сохраняется
            }

            // Лимит итераций: всегда останов (вызовы на cap не исполняются — существующее поведение)
            if (iteration >= maxIterations)
                break;

            var calls = resultCapture.AccumulatedToolCalls;

            if (calls is not { Count: > 0 })
            {
                // Interject-скан (финальный текстовый тур): user-сообщения, добавленные в сессию,
                // пока цикл стримил, подхватываются здесь. Если вмешательств нет — ран завершается.
                (interjects, consumedSessionMessages) = TakeNewUserMessages(targetSession, consumedSessionMessages);
                if (interjects.Count == 0)
                    break;

                // Пользователь вмешался во время финального текстового тура: продолжаем диалог —
                // только что стримившийся текст ассистента остаётся в wire-истории, чтобы модель
                // видела свой ответ перед interject-сообщением.
                if (text.Length > 0)
                    chatMessages.Add(new ChatMessage(ChatRole.Assistant, text.ToString()));
            }
            else
            {
                // Точечный сброс: следующая итерация копит вызовы заново
                resultCapture.AccumulatedToolCalls = null;

                // assistant-сообщение в историю: текст + РОВНО ОДИН FunctionCallContent на вызов
                var contents = new List<AIContent>();
                if (text.Length > 0)
                    contents.Add(new TextContent(text.ToString()));
                foreach (var tc in calls)
                    contents.Add(new FunctionCallContent(tc.Id, tc.Function.Name, ParseArgs(tc.Function.Arguments)));
                chatMessages.Add(new ChatMessage(ChatRole.Assistant, contents));

                // Исполнение КАЖДОГО вызова РОВНО ОДИН РАЗ (последовательно — approval flow)
                foreach (var tc in calls)
                {
                    // Pause gate before each tool execution: a paused branch does not run
                    // the tool even if its approval arrived while paused.
                    if (PauseGate is not null)
                        await PauseGate(cancellationToken);

                    var result = await ExecuteToolForLoopAsync(tc, targetSession, resultCapture, cancellationToken);
                    chatMessages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(tc.Id, result)]));
                }

                // Interject-скан ПОСЛЕ завершения tool-тура, до следующего LLM-вызова:
                // сообщения, добавленные в сессию пока тулзы работали, видны модели уже
                // в следующем вызове.
                (interjects, consumedSessionMessages) = TakeNewUserMessages(targetSession, consumedSessionMessages);
            }

            foreach (var interject in interjects)
                chatMessages.Add(new ChatMessage(ChatRole.User, interject));

            // Начинается новый тур - новое рабочее сообщение для продолжения диалога
            yield return new ChatDelta { IsStartOfTurn = true };
        }
    }

    /// <summary>
    /// Interject-скан: возвращает user-сообщения, добавленные в сессию после индекса
    /// <paramref name="consumed"/> (вмешательства пользователя в работающий агент), в порядке добавления.
    /// Сообщения ассистента пропускаются — цикл сам добавляет их в wire-историю (turn-механика).
    /// </summary>
    /// <returns>Тексты interject-сообщений и новый индекс «прочитанного» (число сообщений сессии).</returns>
    private static (List<string> Interjects, int Consumed) TakeNewUserMessages(ConversationSession session, int consumed)
    {
        var snapshot = session.GetMessagesSnapshot();
        var interjects = new List<string>();
        for (var i = consumed; i < snapshot.Count; i++)
        {
            if (snapshot[i].Role == ChatMessageRole.User)
                interjects.Add(snapshot[i].Content ?? string.Empty);
        }
        return (interjects, snapshot.Count);
    }

    private async Task<string> ExecuteToolForLoopAsync(
        ToolCall toolCall,
        ConversationSession targetSession,
        CompletionsResult resultCapture,
        CancellationToken cancellationToken)
    {
        try
        {
            // Render boundary: yield, чтобы Blazor успел отрендерить блок tool call
            await Task.Yield();
            toolCallHandler.PrepareToolsForApprovals([toolCall]);
            await toolCallHandler.ProcessToolCallsAsync([toolCall], targetSession.Mode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Tool {toolCall.Function.Name} execution failed: {ex.Message}";
        }

        resultCapture.ToolsExecutedByAgent = true;
        return toolCall.Result?.Content ?? "(tool returned no output)";
    }

    private static Dictionary<string, object?>? ParseArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, object?>>(args); }
        catch (JsonException)
        {
            // Truncated JSON (e.g. the stream was cut mid-escape-sequence): repair what we
            // can so the model still sees the partial arguments instead of empty {}.
            return JsonUtils.DeserializePartialAnonymousType(args, new Dictionary<string, object?>())
                is { Count: > 0 } repaired ? repaired : null;
        }
    }
}
