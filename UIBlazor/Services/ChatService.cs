using System.ComponentModel;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using UIBlazor.Services.Models;
using UIBlazor.Services.Settings;

namespace UIBlazor.Services;

public class ChatService(
    HttpClient httpClient,
    IProfileManager profileManager,
    ICommonSettingsProvider commonSettingsProvider,
    IToolManager toolManager,
    ILocalStorageService localStorage,
    ISkillService skillService,
    IRuleService ruleService,
    IVsCodeContextService vsCodeContextService,
    ILogger<IChatService> logger
    ) : IChatService
{
    private const string _thinkStart    = "<think>";
    private const string _thinkEnd      = "</think>";
    private const string _complitions   = "/v1/chat/completions";
    private const string _models        = "/v1/models";
    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ConnectionProfile Options => profileManager.ActiveProfile;

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
            OnSessionChanged?.Invoke(nameof(ConversationSession));
        }
    } = CreateNewSession();

    public event Action<string>? OnSessionChanged;

    private void SessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => OnSessionChanged?.Invoke(e.PropertyName ?? string.Empty);

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

    public async Task AddMessageAsync(VisualChatMessage message)
    {
        Session.AddMessage(message);
        await SaveSessionAsync();
    }

    /// <summary>
    /// Asynchronously saves the current session data to local storage using the session ID as the key.
    /// </summary>
    /// <returns></returns>
    public async Task SaveSessionAsync()
    {
        await localStorage.SetItemAsync(Session.Id, Session);
        UpdateSessionCache(Session);
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

    /// <summary>
    /// Модель, которая последняя отвечала
    /// </summary>
    public string? LastCompletionsModel { get; private set; }

    /// <summary>
    /// Текст ошибки
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Последнее использование токенов
    /// </summary>
    public UsageInfo? LastUsage { get; private set; }

    public string? FinishReason { get; private set; }

    /// <summary>
    /// Asynchronously prepares the system prompt by combining configured instructions, tool usage guidance, skill
    /// metadata, and the current code context.
    /// </summary>
    /// <remarks>
    /// The returned prompt includes information relevant to the current session and code context,
    /// which may affect downstream processing. If no code context is available, the prompt will omit that section. This
    /// method is intended for internal use when constructing prompts for AI interactions.
    /// </remarks>
    /// <returns>
    /// A string containing the complete system prompt, including instructions, tool information, skill details, and
    /// code context if available.
    /// </returns>
    private async Task<string> PrepareSystemPromptAsync(CancellationToken cancellationToken)
    {
        // Загружаем метаданные скиллов и добавляем в системный промпт
        var skillsMetadata = await skillService.GetSkillsMetadataAsync(cancellationToken);
        var skillsSection = skillService.FormatSkillsForSystemPrompt(skillsMetadata);

        var contextSection = new StringBuilder();
        var currentContext = vsCodeContextService.CurrentContext;
        if (currentContext != null)
        {
            contextSection.AppendLine("# CURRENT CODE CONTEXT");

            if (commonSettingsProvider.Current.SendCurrentFile && !string.IsNullOrEmpty(currentContext.ActiveFilePath))
            {
                contextSection.AppendLine($"""
                                          ## Current (active) file
                                          - Path: {currentContext.ActiveFilePath}
                                          - Selected lines: {currentContext.SelectionStartLine} - {currentContext.SelectionEndLine}
                                          - Content:
                                          ```
                                          {currentContext.ActiveFileContent}
                                          ```
                                          """);
            }

            if (commonSettingsProvider.Current.SendSolutionsStricture && currentContext.SolutionFiles.Count != 0)
            {
                contextSection.AppendLine($"""
                                          Solution files:
                                          ```
                                          {string.Join(Environment.NewLine, currentContext.SolutionFiles)}
                                          ```
                                          """);
            }
        }

        // Загружаем правила
        var rules = await ruleService.GetRulesAsync(cancellationToken);
        // файл agents.md
        var agents = await ruleService.GetAgentsMdAsync(cancellationToken);

        List<string?> systemPromptBlocks = [Options.SystemPrompt,
            toolManager.GetToolUseSystemInstructions(Session.Mode, skillsMetadata.Count != 0),
            skillsSection,
            contextSection.ToString(),
            rules,
            !string.IsNullOrEmpty(agents) ? string.Join("# Agents instructions\n", agents) : null];

        return string.Join(Environment.NewLine, systemPromptBlocks.Where(b => !string.IsNullOrEmpty(b)));
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
    public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LastCompletionsModel = null;
        LastUsage = null;
        LastError = null;
        FinishReason = null;

        // Use runtime parameters or fall back to configured options
        var url = $"{Options.Endpoint}{_complitions}";
        var effectiveApiKeyHeader = Options.ApiKeyHeader;

        // Get formatted messages including conversation history
        var messages = Session?.GetFormattedMessages(await PrepareSystemPromptAsync(cancellationToken)) ?? [];

        var payload = new
        {
            model = Options.Model,
            messages = messages,
            temperature = Options.Temperature,
            max_tokens = Options.MaxTokens,
            stream = Options.Stream,
            stream_options = Options.Stream ? new { include_usage = true } : null
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, _jsonSerializerOptions),
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
            var chunk = await response.Content.ReadFromJsonAsync<StreamChunk>(cancellationToken);
            var message = chunk?.Choice?.Message;
            if (message?.Content != null)
            {
                // Удаление <think> блока из контента и перенос его в ReasoningContent если его там нет.
                var regex = Regex.Match(message.Content, $"^{_thinkStart}(?<reason>.*){_thinkEnd}", RegexOptions.Singleline);
                if (regex.Success)
                {
                    message.ReasoningContent ??= regex.Groups["reason"].Value;
                    message.Content = message.Content[regex.Length..];
                }
                LastCompletionsModel ??= chunk?.Model;
                if (chunk?.Usage != null)
                {
                    LastUsage = chunk.Usage;
                    Session.TotalTokens = chunk.Usage.TotalTokens;
                }
                yield return message;
            }
            yield break;
        }

        // стрим
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        var isReasoningContent = false;
        var isStart = true;
        string? role = null;

        // чтобы html-теги <function> склеивать в один чанк
        var _pendingText = string.Empty;
        ChatChoice lastChoise = null!;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null && !cancellationToken.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:"))
            {
                continue;
            }

            var json = line[6..];

            if (json == "[DONE]")
            {
                FinishReason = lastChoise?.FinishReason;
                break;
            }
            
            if (json.StartsWith("{\"error\""))
            {
                LastError = json;
                continue;
            }

            var chunk = JsonUtils.Deserialize<StreamChunk>(json);
            if (chunk == null)
            {
                continue;
            }

            if (chunk.Usage != null)
            {
                LastUsage = chunk.Usage;
                Session.TotalTokens = chunk.Usage.TotalTokens;
            }

            if (chunk.Choices.Count != 1 || chunk.Choices[0].Delta == null)
            {
                continue;
            }

            // Динамический подсчёт токенов во время стрима (приблизительный)
            Session.TotalTokens++;

            LastCompletionsModel ??= chunk.Model;
            lastChoise = chunk.Choices[0];
            var delta = lastChoise.Delta;
            var content = delta!.Content;
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
                        delta.ReasoningContent = content.Replace(_thinkStart, string.Empty);
                        delta.Content = null;
                    }
                }
                else // внутри <think> блока
                {
                    if (content.Contains(_thinkEnd))
                    {
                        // если закончил думать, то можно в контент добавить часть чанка (актуально для Kimi2)
                        isReasoningContent = false;
                        delta.Content = content.Replace(_thinkEnd, string.Empty);
                        delta.ReasoningContent = null;
                    }
                    else
                    {
                        // если не конец - то все пихаем в ReasoningContent и очищаем Content
                        delta.Content = null;
                        delta.ReasoningContent = content;
                    }
                }
            }

            // Если есть контент, то проверяем на разрезанные теги и склеиваем их
            if (delta.Content != null)
            {
                // Склеиваем с остатком от прошлого раза
                var incomingText = _pendingText + delta.Content;
                _pendingText = string.Empty;

                // Ищем последний открывающий тег
                var lastOpenIndex = incomingText.LastIndexOf('<');
                if (lastOpenIndex >= 0)
                {
                    var potentialTag = incomingText[lastOpenIndex..];
                    // Если тег не закрыт (нет '>') и нет переноса строки (\n), то буферизируем
                    if (potentialTag.IndexOfAny(['>', '\n']) == -1)
                    {
                        _pendingText = incomingText[lastOpenIndex..];
                        incomingText = incomingText[..lastOpenIndex];

                        // Если после отрезания тега ничего не осталось, пропускаем итерацию
                        if (string.IsNullOrWhiteSpace(incomingText))
                            continue;
                    }
                }

                delta.Role ??= role;
                delta.Content = incomingText;
            }

            yield return delta;

            isStart = false;
        }

        // если после окончания стрима остался неотправленный текст, отправляем его
        if (!string.IsNullOrEmpty(_pendingText))
        {
            yield return new ChatDelta() { Content = _pendingText };
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
        Session.MaxMessages = Options.MaxMessages;

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
            session.MaxMessages = Options.MaxMessages;
            Session = session;
        }
    }

    public async Task DeleteSessionAsync(string id)
    {
        if (Session?.Id == id)
        {
            Session = CreateNewSession();
            Session.MaxMessages = Options.MaxMessages;
        }
        await localStorage.RemoveItemAsync(id);
        
        _recentSessionsCache?.RemoveAll(s => s.Id == id);
    }

    private async Task<List<string>> GetAllSessionIdsAsync()
    {
        return [.. (await localStorage.GetAllKeysAsync()).Where(k => k.StartsWith("session_"))];
    }

    private static string GenerateSessionId() => $"session_{DateTime.Now:s}";

    private static ConversationSession CreateNewSession() => new () { Id = GenerateSessionId() };


    public async Task LoadLastSessionOrGenerateNewAsync()
    {
        var sessionList = await GetAllSessionIdsAsync();
        // сортируем сессии по времени создания и берем самую свежую
        var lastSessionId = sessionList.OrderByDescending(id =>
        {
            if (DateTime.TryParseExact(id.Substring(8), "s", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            {
                return result;
            }
            return DateTime.MinValue;
        }).FirstOrDefault();
        if (lastSessionId != default)
        {
            var fromStorage = await localStorage.TryGetItemAsync<ConversationSession>(lastSessionId);
            fromStorage?.Id = lastSessionId;
            Session = fromStorage ?? CreateNewSession();
        }
        else
        {
            Session = CreateNewSession();
        }
        Session.MaxMessages = Options.MaxMessages;
    }
}
