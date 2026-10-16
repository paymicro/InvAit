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
        // Объявления only — БЕЗ делегата исполнения (см. правку 2)
        var chatOptions = AfChatClientFactory.BuildChatOptions(
            currentProfile,
            AfToolMapper.Map(tools ?? []));

        using var client = _chatClientFactory(currentProfile, httpClient);
        var maxIterations = MaximumIterations ?? 40; // legacy FICC default

        for (var iteration = 0; ; iteration++)
        {
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

            var calls = resultCapture.AccumulatedToolCalls;
            if (calls is not { Count: > 0 } || iteration >= maxIterations)
                break;

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
                var result = await ExecuteToolForLoopAsync(tc, targetSession, resultCapture, cancellationToken);
                chatMessages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(tc.Id, result)]));
            }

            // Начинается новый тур - новое рабочее сообщение для продолжения диалога
            yield return new ChatDelta { IsStartOfTurn = true };
        }
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
