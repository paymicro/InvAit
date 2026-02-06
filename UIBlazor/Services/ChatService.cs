using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using UIBlazor.Services.Models;
using UIBlazor.Services.Settings;

namespace UIBlazor.Services;

public class ChatService(
    HttpClient httpClient,
    IProfileManager profileManager,
    IToolManager toolManager,
    ILocalStorageService localStorage,
    ISkillService skillService,
    IVsCodeContextService vsCodeContextService
    )
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

    public ConversationSession? Session 
    { 
        get; 
        private set 
        {
            if (field != value)
            {
                field = value;
                NotifySessionChanged();
            }
        }
    }

    public event Action? OnSessionChanged;

    private void NotifySessionChanged() => OnSessionChanged?.Invoke();

    /// <summary>
    /// Determines if an exception is retryable (network errors, timeouts, etc.)
    /// </summary>
    private static bool IsRetryableException(Exception ex)
    {
        return ex is HttpRequestException ||
               ex is TaskCanceledException ||
               ex is TimeoutException ||
               (ex is JsonException && ex.Message.Contains("deserialization", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Executes an async operation with retry logic
    /// </summary>
    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        string operationName = "operation")
    {
        var maxRetries = Options.MaxRetryAttempts;
        var retryDelay = TimeSpan.FromSeconds(Options.RetryDelaySeconds);

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxRetries && IsRetryableException(ex) && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == maxRetries || !IsRetryableException(ex))
            {
                throw new Exception($"{operationName} failed after {attempt + 1} attempts: {ex.Message}", ex);
            }
        }

        throw new InvalidOperationException("Unexpected end of retry loop");
    }

    /// <summary>
    /// Получение списка моделей по API
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="HttpRequestException"></exception>
    /// <exception cref="JsonException"></exception>
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

    public async Task AddMessageAsync(string role, string content)
    {
        Session.AddMessage(role, content);
        await SaveSessionAsync();
    }

    public async Task AddMessageAsync(VisualChatMessage message)
    {
        Session.AddMessage(message);
        await SaveSessionAsync();
    }

    public async Task SaveSessionAsync()
    {
        await localStorage.SetItemAsync(Session.Id, Session);
    }

    /// <summary>
    /// Модель, которая последняя отвечала
    /// </summary>
    public string? LastCompletionsModel { get; private set; }

    /// <summary>
    /// Последнее использование токенов
    /// </summary>
    public UsageInfo? LastUsage { get; private set; }

    /// <summary>
    /// Asynchronously prepares the system prompt by combining configured instructions, tool usage guidance, skill
    /// metadata, and the current code context.
    /// </summary>
    /// <remarks>The returned prompt includes information relevant to the current session and code context,
    /// which may affect downstream processing. If no code context is available, the prompt will omit that section. This
    /// method is intended for internal use when constructing prompts for AI interactions.</remarks>
    /// <returns>A string containing the complete system prompt, including instructions, tool information, skill details, and
    /// code context if available.</returns>
    private async Task<string> PrepareSystemPromptAsync()
    {
        // Загружаем метаданные скиллов и добавляем в системный промпт
        var skillsMetadata = await skillService.GetSkillsMetadataAsync();
        var skillsSection = skillService.FormatSkillsForSystemPrompt(skillsMetadata);

        var contextSection = string.Empty;
        var currentContext = vsCodeContextService.CurrentContext;
        if (currentContext != null && !string.IsNullOrEmpty(currentContext.ActiveFilePath))
        {
            contextSection = $"""
            
            # CURRENT CODE CONTEXT
            File: {currentContext.ActiveFilePath}
            Selection lines: {currentContext.SelectionStartLine} - {currentContext.SelectionEndLine}
            
            ```
            {currentContext.ActiveFileContent}
            ```

            Solution files:
            ```
            {string.Join(Environment.NewLine, currentContext.SolutionFiles)}
            ```
            """;
        }

        return string.Join(Environment.NewLine,
            Options.SystemPrompt,
            toolManager.GetToolUseSystemInstructions(Session?.Mode ?? AppMode.Chat),
            skillsSection,
            contextSection);
    }

    public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LastCompletionsModel = null;
        LastUsage = null;

        // Use runtime parameters or fall back to configured options
        var url = $"{Options.Endpoint}{_complitions}";
        var effectiveApiKey = Options.ApiKey;
        var effectiveApiKeyHeader = Options.ApiKeyHeader;

        // Get formatted messages including conversation history
        var messages = Session?.GetFormattedMessages(await PrepareSystemPromptAsync()) ?? [];

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

        if (!string.IsNullOrEmpty(effectiveApiKey))
        {
            if (string.Equals(effectiveApiKeyHeader, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", effectiveApiKey);
            }
            else
            {
                request.Headers.Add(effectiveApiKeyHeader, effectiveApiKey);
            }
        }

        var response = await ExecuteWithRetryAsync(async ct => 
            await httpClient.SendAsync(request, Options.Stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct), 
            cancellationToken, "Chat completion").ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var message = Options.Stream ? "stream" : "request";
            throw new Exception($"Chat {message} failed: {await response.Content.ReadAsStringAsync(cancellationToken)}");
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

        var assistantResponse = new StringBuilder();

        string? line;
        var isReasoningContent = false;
        var isStart = true;
        
        while ((line = await reader.ReadLineAsync()) is not null && !cancellationToken.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:"))
            {
                continue;
            }

            var json = line["data:".Length..].Trim();

            if (json == "[DONE]")
            {
                break;
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

            LastCompletionsModel ??= chunk.Model;
            var delta = chunk.Choices[0].Delta;
            var content = delta!.Content;

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
                    else
                    {
                        // Не думали - нечего и начинать. Пишем чистый контент в историю
                        assistantResponse.Append(content);
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

            yield return delta;

            isStart = false;
        }
    }

    private async Task<List<string>> GetAllSessionIdsAsync()
    {
        return [.. (await localStorage.GetAllKeysAsync()).Where(k => k.StartsWith("session_"))];
    }

    private string GenerateSessionId() => $"session_{DateTime.Now:s}";

    private ConversationSession CreateNewSession() => new() { Id = GenerateSessionId() };

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
            var fromStorage = await localStorage.GetItemAsync<ConversationSession>(lastSessionId);
            fromStorage?.Id = lastSessionId;
            Session = fromStorage ?? CreateNewSession();
        }
        else
        {
            Session = CreateNewSession();
        }
        Session.MaxMessages = Options.MaxMessages;
    }

    public async Task ClearSessionAsync()
    {
        await localStorage.RemoveItemAsync(Session.Id);
        Session = CreateNewSession();
    }
}