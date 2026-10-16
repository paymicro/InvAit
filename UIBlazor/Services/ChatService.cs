using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using UIBlazor.Services.AgentFramework;

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

    // LLM gateways (Microsoft Agent Framework paths):
    // - AfAgentLoop: the tool-invocation loop with tool execution routed through ToolCallHandler
    //   (main agent chat and sub-agent branches via AfSubAgentOrchestrator);
    // - AfCompletionsAdapter: single-shot IChatClient gateway without automatic tool execution
    //   (context compression).
    private readonly AfCompletionsAdapter _afAdapter =
        new(() => profileManager.ActiveProfile, httpClient, () => commonSettingsProvider.Current.HttpTimeoutSec);

    private readonly AfAgentLoop _afLoop =
        new(() => profileManager.ActiveProfile, httpClient, toolCallHandler);


    public ConnectionProfile Options => profileManager.ActiveProfile;

    /// <summary>
    /// Подписка на события после создания экземпляра
    /// </summary>
    public void Initialize()
    {
        Session.PropertyChanged -= SessionPropertyChanged;
        Session.PropertyChanged += SessionPropertyChanged;
    }

    public ConversationSession Session
    {
        get;
        private set
        {
            if (field == value)
                return;
            field?.PropertyChanged -= SessionPropertyChanged;
            field = value;
            field?.PropertyChanged += SessionPropertyChanged;
            SessionChanged?.Invoke(field, new PropertyChangedEventArgs(nameof(ConversationSession)));
        }
    } = CreateNewSession();

    public event PropertyChangedEventHandler? SessionChanged;

    private void SessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => SessionChanged?.Invoke(sender, e);

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

    public bool NeedCompression => Options.TokensToCompress > 0 && Session.TotalTokens > Options.TokensToCompress;

    public Task ProcessStreamAsync(
        VisualChatMessage message,
        IAsyncEnumerable<ChatDelta> deltas,
        Action<string>? onContentUpdate,
        Action<List<ToolCall>>? onToolCallsUpdate,
        Action? onStateChange,
        CompletionsResult resultCapture,
        CancellationToken cancellationToken)
        => ProcessStreamAsync(message, deltas, onContentUpdate, onToolCallsUpdate, onStateChange, resultCapture, cancellationToken, onNewTurn: null);

    public async Task ProcessStreamAsync(
        VisualChatMessage message,
        IAsyncEnumerable<ChatDelta> deltas,
        Action<string>? onContentUpdate,
        Action<List<ToolCall>>? onToolCallsUpdate,
        Action? onStateChange,
        CompletionsResult resultCapture,
        CancellationToken cancellationToken,
        Func<VisualChatMessage>? onNewTurn)
    {
        var sw = Stopwatch.StartNew();
        var reasoning = new StringBuilder();
        var response = new StringBuilder();
        double firstTokenMs = 0;
        double firstContentTokenMs = 0;

        message.Timings ??= new MessageTimings { Tokens = 0 };
        // Agent-loop run: creating the next turn's message is deferred until that
        // turn actually produces output, so a blank (or cancelled) turn leaves no empty bubble.
        var pendingNewTurn = false;

        await foreach (var delta in deltas.WithCancellation(cancellationToken))
        {
            var hasPayload = !string.IsNullOrEmpty(delta.Content)
                             || !string.IsNullOrEmpty(delta.ReasoningContent)
                             || delta.ToolCalls is { Count: > 0 };

            // Agent-loop run: a new assistant turn started - close the previous
            // message (it stays as a completed part of the answer) and wait for its first payload.
            if (delta.IsStartOfTurn && onNewTurn is not null && message.IsStreaming)
            {
                message.IsStreaming = false;
                CalcTimings(message, sw, firstTokenMs, resultCapture);
                pendingNewTurn = true;
                reasoning.Clear();
                response.Clear();
                firstTokenMs = sw.ElapsedMilliseconds;
                firstContentTokenMs = 0;
            }

            if (pendingNewTurn && hasPayload)
            {
                message = onNewTurn();
                message.Timings ??= new MessageTimings { Tokens = 0 };
                pendingNewTurn = false;
            }

            if (!hasPayload)
            {
                // Marker-only delta (turn boundary) or an empty update: nothing to render yet.
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

        // Финальный подсчет таймингов. Нужен т.к. токен с Usage не запускает цикл выше (он без дельты).
        // IsStreaming сбрасывается ДО расчета: завершенный бейдж должен показать видимые токены.
        message.IsStreaming = false;
        CalcTimings(message, sw, firstTokenMs, resultCapture);
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
    /// Сжатие контекста основной сессии: последние сообщения остаются дословно,
    /// история текст заменяется сводкой от LLM (resultCapture — состояние LLM-вызова).
    /// </summary>
    public async IAsyncEnumerable<ChatDelta> CompressSessionAsync(
        CompletionsResult resultCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (Messages, LastUserMessage) = Session.GetFormattedMessagesForCompress(contentFilter);

        // Получаем сжатый текст от LLM (compression runs WITHOUT tools - single-shot adapter)
        var contentSb = new StringBuilder();
        resultCapture.Reset();
        await foreach (var chatDelta in _afAdapter.GetCompletionsAsync(Messages, null, Session, resultCapture, cancellationToken))
        {
            if (chatDelta.Content is not null)
            {
                contentSb.Append(chatDelta.Content);
            }
            yield return chatDelta;
        }

        if (cancellationToken.IsCancellationRequested)
            yield break;

        // Создаем новый объект сообщения со сжатым контекстом
        var compressedMessage = new VisualChatMessage
        {
            Content = contentSb.ToString(),
            Role = ChatMessageRole.Assistant,
            IsExpanded = true
        };

        var snapshot = Session.GetMessagesSnapshot();
        var totalCount = snapshot.Count;
        var windowSize = totalCount < 6 ? 2 : 3;

        var topMessages = new List<VisualChatMessage>();
        var bottomMessages = new List<VisualChatMessage>();

        for (var i = 0; i < totalCount - 1; i++)
        {
            var msg = snapshot[i];

            if (msg.Id == LastUserMessage?.Id)
                continue;

            // Убираем результаты тулзов в сообщениях
            if (msg.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in msg.ToolCalls)
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

            // Первые сообщения
            if (i < windowSize)
            {
                topMessages.Add(msg);
            }

            // Оставшиеся сообщения
            else if (i >= totalCount - 1 - windowSize)
            {
                bottomMessages.Add(msg);
            }
        }

        var keptMessages = new List<VisualChatMessage>(topMessages.Count + bottomMessages.Count + 2);
        keptMessages.AddRange(topMessages);
        keptMessages.AddRange(bottomMessages);
        keptMessages.Add(compressedMessage);

        // Восстанавливаем сообщение пользователя после компрессии
        if (LastUserMessage is not null)
        {
            keptMessages.Add(LastUserMessage);
        }

        // Перезаписываем историю
        Session.SetMessages(keptMessages);
    }

    /// <summary>
    /// Asynchronously saves the current session data to local storage using the session ID as the key.
    /// </summary>
    /// <returns></returns>
    public async Task SaveSessionAsync()
    {
        try
        {
            await localStorage.SetItemAsync(Session.Id, Session);
            UpdateSessionCache(Session);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to save session {id}: {message}", Session.Id, ex.Message);
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

    private async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
        IReadOnlyList<WireMessage> messages,
        bool withTools,
        CompletionsResult resultCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        resultCapture.Reset();

        var tools = withTools ? toolManager.GetEnabledTools(Session.Mode) : null;

        // Runs WITH tools go through the tool-invocation loop (AfAgentLoop); runs WITHOUT
        // tools (context compression) go through the single-shot adapter. Sub-agent
        // branches build their own AfAgentLoop (AfSubAgentOrchestrator.RunBranchAsync).
        ICompletionsGateway gateway = withTools ? _afLoop : _afAdapter;

        await foreach (var delta in gateway.GetCompletionsAsync(messages, tools, Session, resultCapture, cancellationToken))
        {
            yield return delta;
        }
    }

    /// <summary>
    /// Asynchronously generates a sequence of chat completion deltas for the current conversation session.
    /// </summary>
    /// <remarks>
    /// This method streams chat completion results as they become available, allowing for real-time
    /// processing of partial responses. The returned sequence may include reasoning content or message content
    /// depending on the model and response format. If streaming is not enabled, the method yields a single completion
    /// result.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <returns>
    /// An asynchronous stream of <see cref="ChatDelta"/> objects representing incremental updates to the chat
    /// completion. The stream completes when the response is fully received.
    /// </returns>
    /// <exception cref="Exception">Thrown if the chat completion request fails or the server returns an unsuccessful response.</exception>
    public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(CompletionsResult resultCapture, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Get formatted messages including conversation history
        var messages = Session.GetFormattedMessages(await systemPromptBuilder.PrepareSystemPromptAsync(Session.Mode, cancellationToken), contentFilter);

        await foreach (var chatDelta in GetCompletionsAsync(messages, true, resultCapture, cancellationToken))
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
        // Save current session if it has messages
        if (Session?.GetMessageCount() > 0)
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
        Session = await TryLoadSessionAsync(id) ?? Session;
    }

    public async Task DeleteSessionAsync(string id)
    {
        if (Session?.Id == id)
        {
            Session = CreateNewSession();
        }
        await localStorage.RemoveItemAsync(id);

        _recentSessionsCache?.RemoveAll(s => s.Id == id);
    }

    private async Task<List<string>> GetAllSessionIdsAsync()
    {
        return [.. (await localStorage.GetAllKeysAsync()).Where(k => k.StartsWith("session_"))];
    }

    private static string GenerateSessionId() => $"session_{DateTime.Now:s}";

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
        => DateTime.TryParseExact(id[8..], "s", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result
            : DateTime.MinValue;

    public void Dispose()
    {
        Session.PropertyChanged -= SessionPropertyChanged;
    }
}
