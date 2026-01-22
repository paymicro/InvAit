using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UIBlazor.Models;
using UIBlazor.Options;
using UIBlazor.Services.Models;
using UIBlazor.Utils;

namespace UIBlazor.Services;

public class ChatService(
    HttpClient httpClient,
    AiSettingsProvider aiSettingsProvider,
    ToolManager toolManager,
    LocalStorageService localStorage
    )
{
    private const string _thinkStart = "<think>";
    private const string _thinkEnd = "</think>";
    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AiOptions Options => aiSettingsProvider.Current;

    public async Task<AiModelList> GetModelsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{aiSettingsProvider.Current.Endpoint}/v1/models");
        
        if (!string.IsNullOrEmpty(aiSettingsProvider.Current.ApiKey))
        {
            if (string.IsNullOrWhiteSpace(aiSettingsProvider.Current.ApiKeyHeader))
            {
                throw new InvalidOperationException("API key header must be specified when an API key is provided.");
            }

            if (string.Equals(aiSettingsProvider.Current.ApiKeyHeader, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aiSettingsProvider.Current.ApiKey);
            }
            else
            {
                request.Headers.Add(aiSettingsProvider.Current.ApiKeyHeader, aiSettingsProvider.Current.ApiKey);
            }
        }

        if (string.IsNullOrEmpty(aiSettingsProvider.Current.Endpoint))
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

    public async Task AddMessageAsync(string sessionId, string role, string content)
    {
        // Get or create session
        var session = await GetOrCreateSessionAsync(sessionId);
        session.AddMessage(role, content);
        await localStorage.SetItemAsync(sessionId, session);
    }

    public string? LastCompletionsModel { get; private set; }

    public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Get or create session
        var session = await GetOrCreateSessionAsync(sessionId);
        session.MaxMessages = Options.MaxMessages;

        LastCompletionsModel = null;

        // Use runtime parameters or fall back to configured options
        var url = $"{Options.Endpoint}/v1/chat/completions";
        var effectiveApiKey = Options.ApiKey;
        var effectiveApiKeyHeader = Options.ApiKeyHeader;

        var systemPrompt = toolManager.GetToolUseSystemInstructions(Options.SystemPrompt);
        // Get formatted messages including conversation history
        var messages = session.GetFormattedMessages(systemPrompt);

        var payload = new
        {
            model = Options.Model,
            messages = messages,
            temperature = Options.Temperature,
            max_tokens = Options.MaxTokens,
            stream = Options.Stream,
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, _jsonSerializerOptions),
                Encoding.UTF8,
                System.Net.Mime.MediaTypeNames.Application.Json)
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

        var response = await httpClient.SendAsync(
            request, 
            Options.Stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
            cancellationToken);

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
                    message.Content = message.Content.Remove(0, regex.Length);
                }
                LastCompletionsModel ??= chunk?.Model;
                yield return message;
            }
            yield break;
        }

        // стрим
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
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
            if (chunk == null || chunk.Choices.Count != 1 || chunk.Choices[0].Delta == null)
            {
                break;
            }

            LastCompletionsModel ??= chunk.Model;
            var delta = chunk.Choices[0].Delta;
            var content = delta.Content;

            // Размышляющие модели по разному отдают размышления
            //
            //           ReasoningContent | Content
            // GLM 4.7         +++        |   ---
            // Kimi 2          +++        | <think>
            // Deepseek R1     ---        | <think>
            //
            // обрабатываем размышления как Z.ai GLM.
            // Все размышления идут в ReasoningContent с пустым Content

            if (delta.ReasoningContent == null && !string.IsNullOrEmpty(content))
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
                        // Не думали - нечего и начинать.
                        // Пишем чистый контент в историю
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
                    }
                    else
                    {
                        // если не конец - то все пихаем в ReasoningContent
                        delta.ReasoningContent = content;
                        delta.Content = null;
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

    public async Task<ConversationSession> GetOrCreateSessionAsync(string sessionId)
    {
        var sessionList = await GetAllSessionIdsAsync();
        ConversationSession session = sessionList.Contains(sessionId)
            ? await localStorage.GetItemAsync<ConversationSession>(sessionId) ?? new ()
            : new ();

        // не храним Id в базе
        session.Id = sessionId;
        return session;
    }

    public async Task ClearSessionAsync(string sessionId)
        => await localStorage.RemoveItemAsync(sessionId);
}