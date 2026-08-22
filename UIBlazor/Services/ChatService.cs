using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Runtime.CompilerServices;

namespace UIBlazor.Services;

public class ChatService(
    HttpClient httpClient,
    IProfileManager profileManager,
    ISystemPromptBuilder systemPromptBuilder,
    ILocalStorageService localStorage,
    ILogger<IChatService> logger,
    IToolManager toolManager
    ) : IChatService
{
    #pragma warning disable format
    private const string _thinkStart    = "<think>";
    private const string _thinkEnd      = "</think>";
    private const string _complitions   = "/v1/chat/completions";
    private const string _models        = "/v1/models";
    #pragma warning restore format

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
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Options.Endpoint}{_models}");

        if (!string.IsNullOrEmpty(Options.ApiKey))
        {
            if (string.IsNullOrWhiteSpace(Options.ApiKeyHeader))
            {
                throw new InvalidOperationException("API key header must be specified when an API key is provided.");
            }

            if (string.Equals(Options.ApiKeyHeader, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.ApiKey);
            }
            else
            {
                request.Headers.Add(Options.ApiKeyHeader, Options.ApiKey);
            }
        }

        if (string.IsNullOrEmpty(Options.Endpoint))
        {
            throw new InvalidOperationException("Endpoint must be specified.");
        }

        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Getting models failed: {await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");
        }

        return await response.Content.ReadFromJsonAsync<AiModelList>(cancellationToken)
               ?? throw new JsonException("Models deserialization exception");
    }

    public bool NeedCompression => Options.TokensToCompress > 0 && Session.TotalTokens > Options.TokensToCompress;

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
        await foreach (var delta in deltas.WithCancellation(cancellationToken))
        {
            if (firstTokenMs == 0)
                firstTokenMs = sw.ElapsedMilliseconds;

            if (!string.IsNullOrEmpty(delta.ReasoningContent))
            {
                reasoning.Append(delta.ReasoningContent);
                message.ReasoningContent = reasoning.ToString();
            }

            if (!string.IsNullOrEmpty(delta.Content))
            {
                if (firstContentTokenMs == 0)
                {
                    firstContentTokenMs = sw.ElapsedMilliseconds;
                    message.Timings.Reasoning = TimeSpan.FromMilliseconds(firstContentTokenMs - firstTokenMs);
                }

                response.Append(delta.Content);
                message.Content = response.ToString();
                onContentUpdate?.Invoke(delta.Content);
            }

            if (delta.ToolCalls is { Count: > 0 } && resultCapture.AccumulatedToolCalls is not null)
            {
                if (firstContentTokenMs == 0)
                {
                    firstContentTokenMs = sw.ElapsedMilliseconds;
                    message.Timings.Reasoning = TimeSpan.FromMilliseconds(firstContentTokenMs - firstTokenMs);
                }

                onToolCallsUpdate?.Invoke(resultCapture.AccumulatedToolCalls);
            }

            CalcTimings(message, sw, firstTokenMs, resultCapture.Usage);

            if (firstContentTokenMs > 0)
            {
                message.Timings.Content = TimeSpan.FromMilliseconds(sw.ElapsedMilliseconds - firstContentTokenMs);
            }

            onStateChange?.Invoke();
        }

        CalcTimings(message, sw, firstTokenMs, resultCapture.Usage);
        message.IsStreaming = false;
    }

    private static void CalcTimings(VisualChatMessage message, Stopwatch sw, double firstTokenMs, UsageInfo? usage)
    {
        var elapsedMs = sw.ElapsedMilliseconds;
        message.Timings.Tokens = (usage?.CompletionTokens ?? 0) + message.Timings.Tokens;
        var secForTokens = Math.Max(1, (elapsedMs - firstTokenMs) / 1000.0);
        message.Timings.TokensInSec = (float)(message.Timings.Tokens / secForTokens);
        message.Timings.Total = TimeSpan.FromMilliseconds(elapsedMs);
        message.Timings.FirstToken = TimeSpan.FromMilliseconds(firstTokenMs);
    }

    public async IAsyncEnumerable<ChatDelta> CompressSessionAsync(CompletionsResult resultCapture, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var delta in CompressSessionAsync(Session, resultCapture, cancellationToken))
        {
            yield return delta;
        }
    }

    /// <summary>
    /// Сжатие контекста для произвольной сессии (например, sub-agent).
    /// </summary>
    public async IAsyncEnumerable<ChatDelta> CompressSessionAsync(
        ConversationSession session,
        CompletionsResult resultCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (Messages, LastUserMessage) = session.GetFormattedMessagesForCompress();

        // Получаем сжатый текст от LLM
        var contentSb = new StringBuilder();
        await foreach (var chatDelta in GetCompletionsAsync(Messages, false, session, null, resultCapture, cancellationToken))
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

        var totalCount = session.Messages.Count;
        var windowSize = totalCount < 6 ? 2 : 3;

        var topMessages = new List<VisualChatMessage>();
        var bottomMessages = new List<VisualChatMessage>();

        for (var i = 0; i < totalCount - 1; i++)
        {
            var msg = session.Messages[i];

            if (msg.Id == LastUserMessage?.Id)
                continue;

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
        session.Messages = keptMessages;
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
        var firstMessage = session.Messages.FirstOrDefault(m => m.Role == ChatMessageRole.User)?.Content ?? string.Empty;
        var preview = firstMessage is { Length: > 40 } ? firstMessage[..40] + "..." : firstMessage;

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

    private async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
        IEnumerable<object> messages,
        bool withTools,
        CompletionsResult resultCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var delta in GetCompletionsAsync(messages, withTools, Session, null, resultCapture, cancellationToken))
        {
            yield return delta;
        }
    }

    /// <summary>
    /// Internal completions method that supports a custom session (for sub-agents).
    /// All LLM call state (model, usage, tool_calls, finish_reason, error) is written
    /// exclusively to <paramref name="resultCapture"/>, never to instance properties.
    /// This ensures isolation between main agent and sub-agent calls.
    /// </summary>
    private async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
        IEnumerable<object> messages,
        bool withTools,
        ConversationSession targetSession,
        IEnumerable<Tool>? customTools,
        CompletionsResult resultCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        resultCapture.Reset();

        // Use runtime parameters or fall back to configured options
        var url = $"{Options.Endpoint}{_complitions}";
        var effectiveApiKeyHeader = Options.ApiKeyHeader;

        var payload = new Dictionary<string, object>
        {
            { "model", Options.Model },
            { "messages", messages },
            { "temperature", Options.Temperature },
            { "stream", Options.Stream },
            { "stream_options", new { include_usage = true } }
        };

        if (Options.MaxTokens >= 1000)
        {
            payload.Add("max_tokens", Options.MaxTokens);
            payload.Add("max_completion_tokens", Options.MaxTokens);
        }

        if (!string.IsNullOrEmpty(Options.ExtraPayload))
        {
            try
            {
                var extra = JsonSerializer.Deserialize<Dictionary<string, object>>(Options.ExtraPayload);
                if (extra != null)
                {
                    foreach (var kvp in extra)
                    {
                        payload[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (JsonException)
            {
                // skip invalid ExtraPayload
            }
        }

        // Add native tool definitions
        if (withTools)
        {
            var enabledTools = (customTools ?? (targetSession == Session
                ? toolManager.GetEnabledTools(Session.Mode)
                : toolManager.GetEnabledTools(AppMode.Agent))).Select(t => t.NativeTool).ToList();
            if (enabledTools.Count > 0)
            {
                payload["tools"] = enabledTools;
                payload["tool_choice"] = "auto";
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        if (!string.IsNullOrEmpty(Options.ApiKey))
        {
            if (string.Equals(effectiveApiKeyHeader, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.ApiKey);
            }
            else
            {
                request.Headers.Add(effectiveApiKeyHeader, Options.ApiKey);
            }
        }

        foreach (var header in Options.ExtraHeaders.Where(h => !string.IsNullOrEmpty(h.Name)))
        {
            request.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        var response = await httpClient.SendAsync(request, Options.Stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var result = $"HttpCode: {response.StatusCode} | server failed: {await response.Content.ReadAsStringAsync(cancellationToken)}";
            throw new Exception(result);
        }

        // если не стрим, то возвращаем как один чанк
        if (!Options.Stream)
        {
            // Reset accumulated tool calls
            resultCapture.AccumulatedToolCalls = null;

            var chunk = await response.Content.ReadFromJsonAsync<StreamChunk>(cancellationToken);
            var message = chunk?.Choice?.Message;

            resultCapture.Model ??= chunk?.Model;
            if (chunk?.Usage != null)
            {
                resultCapture.Usage = chunk.Usage;
                targetSession.TotalTokens = chunk.Usage.TotalTokens;
            }

            if (message?.ToolCalls is { Count: > 0 })
            {
                // Native tool calls in non-streaming response
                resultCapture.AccumulatedToolCalls = [.. message.ToolCalls];
                resultCapture.FinishReason = chunk?.Choice?.FinishReason ?? "tool_calls";
                yield return message;
                yield break;
            }

            if (message?.Content != null)
            {
                // Удаление <think> блока из контента и перенос его в ReasoningContent если его там нет.
                var regex = Regex.Match(message.Content, $"^{_thinkStart}(?<reason>.*){_thinkEnd}", RegexOptions.Singleline);
                if (regex.Success)
                {
                    message.ReasoningContent ??= regex.Groups["reason"].Value;
                    message.Content = message.Content[regex.Length..];
                }
                yield return message;
            }
            yield break;
        }

        // стрим
        // Reset accumulated tool calls for this response
        resultCapture.AccumulatedToolCalls = null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // Accumulator for partial tool_calls arguments (keyed by index)
        var toolCallAcc = new Dictionary<int, ToolCall>();

        var isReasoningContent = false;
        var isStart = true;
        string? role = null;

        ChatChoice lastChoise = null!;
        while (await reader.ReadLineAsync(cancellationToken) is { } line && !cancellationToken.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:"))
            {
                continue;
            }

            var json = line[6..];

            if (json == "[DONE]")
            {
                resultCapture.FinishReason = lastChoise?.FinishReason;
                break;
            }

            if (json.StartsWith("{\"error\""))
            {
                resultCapture.Error = json;
                continue;
            }

            var chunk = JsonUtils.Deserialize<StreamChunk>(json);
            if (chunk == null)
            {
                continue;
            }

            if (chunk.Usage != null)
            {
                resultCapture.Usage = chunk.Usage;
                targetSession.TotalTokens = chunk.Usage.TotalTokens;
            }
            else
            {
                // Приблизительная оценка токенов во время стрима до получения usage data.
                // Стандартная эвристика: ~4 символа на токен.
                // Используется только до финального чанка с usage, который перезапишет
                // TotalTokens точным значением. Это предотвращает сильное завышение
                // счётчика (раньше каждый chunk добавлял +1) и преждевременный триггер
                // token budget limit в субагентах и компрессии контекста.
                var estDelta = chunk.Choices.Count == 1 ? chunk.Choices[0].Delta : null;
                var estimatedChars = (estDelta?.Content?.Length ?? 0)
                                   + (estDelta?.ReasoningContent?.Length ?? 0)
                                   + (estDelta?.Reasoning?.Length ?? 0);
                if (estimatedChars > 0)
                    targetSession.TotalTokens += Math.Max(1, estimatedChars / 4);
            }

            resultCapture.Model ??= chunk.Model;

            if (chunk.Choices.Count != 1 || chunk.Choices[0].Delta == null)
            {
                continue;
            }

            var delta = chunk.Choices[0].Delta!;
            var content = delta.Content;
            role ??= delta?.Role;

            // Размышляющие модели по разному отдают размышления
            //
            //           ReasoningContent | Content
            // GLM 4.7         +++        |   ---
            // Kimi 2          +++        | <think>
            // Deepseek R1     ---        | <think>
            //
            // обрабатываем размышления как Z.ai GLM.
            // Все размышления идут в ReasoningContent с пустым Content

            // Преобразрвания нужны если есть контент с блоком <think>
            if (!string.IsNullOrEmpty(content))
            {
                if (!isReasoningContent) // не думаем
                {
                    if (isStart && content.StartsWith(_thinkStart))
                    {
                        // начать думать можно только в первом чанке
                        isReasoningContent = true;
                        delta!.ReasoningContent = content.Replace(_thinkStart, string.Empty);
                        delta.Content = null;
                    }
                }
                else // внутри <think> блока
                {
                    if (content.Contains(_thinkEnd))
                    {
                        // если закончил думать, то можно в контент добавить часть чанка (актуально для Kimi2)
                        isReasoningContent = false;
                        delta!.Content = content.Replace(_thinkEnd, string.Empty);
                        delta.ReasoningContent = null;
                    }
                    else
                    {
                        // если не конец - то все пихаем в ReasoningContent и очищаем Content
                        delta!.Content = null;
                        delta.ReasoningContent = content;
                    }
                }
            }

            // Accumulate native tool_calls across chunks
            if (delta.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in delta.ToolCalls)
                {
                    var idx = tc.Index ?? 0;
                    if (!toolCallAcc.TryGetValue(idx, out var existing))
                    {
                        existing = new ToolCall
                        {
                            Index = idx,
                            Function = new ToolCallFunction(),
                            IsReady = false,
                            ApprovalStatus = ToolApprovalStatus.Pending
                        };
                        toolCallAcc[idx] = existing;
                    }
                    if (!string.IsNullOrEmpty(tc.Id))
                        existing.Id = tc.Id;
                    if (!string.IsNullOrEmpty(tc.Type))
                        existing.Type = tc.Type;
                    if (!string.IsNullOrEmpty(tc.Function?.Name))
                        existing.Function.Name = tc.Function.Name;
                    if (!string.IsNullOrEmpty(tc.Function?.Arguments))
                        existing.Function.Arguments += tc.Function.Arguments;
                }
                resultCapture.AccumulatedToolCalls = [.. toolCallAcc.Values];
            }

            yield return delta;

            isStart = false;
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
        var messages = Session.GetFormattedMessages(await systemPromptBuilder.PrepareSystemPromptAsync(Session.Mode, cancellationToken)) ?? [];

        await foreach (var chatDelta in GetCompletionsAsync(messages, true, resultCapture, cancellationToken))
        {
            yield return chatDelta;
        }
    }

    /// <summary>
    /// Получение ответа для sub-agent с произвольным системным промптом и набором инструментов.
    /// Использует переданную сессию вместо основной.
    /// Состояние (AccumulatedToolCalls, Usage и т.д.) записывается в <paramref name="resultCapture"/>,
    /// а не в общие свойства экземпляра, чтобы избежать конфликтов с главным агентом.
    /// </summary>
    public async IAsyncEnumerable<ChatDelta> GetCompletionsForSubAgentAsync(
        ConversationSession session,
        string systemPrompt,
        IEnumerable<Tool> enabledTools,
        CompletionsResult resultCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messages = session.GetFormattedMessages(systemPrompt);

        await foreach (var chatDelta in GetCompletionsAsync(messages, true, session, enabledTools, resultCapture, cancellationToken))
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
            var firstMessage = session?.Messages.FirstOrDefault(m => m.Role == ChatMessageRole.User)?.Content;
            if (session != null && firstMessage != null)
            {
                var preview = firstMessage.Length > 40 ? firstMessage[..40] + "..." : firstMessage;

                summaries.Add(new SessionSummary
                {
                    Id = id,
                    CreatedAt = session.CreatedAt,
                    FirstUserMessage = preview
                });
            }
            else
            {
                await localStorage.RemoveItemAsync(id);
                logger.LogError("Invalid session {id} is removed", id);
            }
        }

        _recentSessionsCache = [.. summaries.OrderByDescending(s => s.CreatedAt)];

        return [.. _recentSessionsCache.Take(count)];
    }

    public async Task NewSessionAsync()
    {
        // Save current session if it has messages
        if (Session?.Messages.Count > 0)
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

    public async Task LoadSessionAsync(string id)
    {
        var session = await localStorage.TryGetItemAsync<ConversationSession>(id);
        if (session != null)
        {
            session.Id = id;
            Session = session;
        }
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
        var sessionList = await GetAllSessionIdsAsync();
        // сортируем сессии по времени создания и берем самую свежую
        var lastSessionId = sessionList.OrderByDescending(id =>
            DateTime.TryParseExact(id[8..], "s", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
                ? result
                : DateTime.MinValue).FirstOrDefault();
        if (lastSessionId is not null)
        {
            var fromStorage = await localStorage.TryGetItemAsync<ConversationSession>(lastSessionId);
            fromStorage?.Id = lastSessionId;
            Session = fromStorage ?? CreateNewSession();
        }
        else
        {
            Session = CreateNewSession();
        }
    }

    public void Dispose()
    {
        Session.PropertyChanged -= SessionPropertyChanged;
    }
}
