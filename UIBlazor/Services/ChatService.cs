using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UIBlazor.Models;
using UIBlazor.Options;
using UIBlazor.Services.Models;
using UIBlazor.Utils;

namespace UIBlazor.Services;

public class ChatService(
    IServiceProvider serviceProvider,
    AiSettingsProvider aiSettingsProvider,
    ToolManager toolManager,
    LocalStorageService localStorage
    )
{
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
        
        var httpClient = serviceProvider.GetRequiredService<HttpClient>();
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

    public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Get or create session
        var session = await GetOrCreateSessionAsync(sessionId);

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
            stream = true
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }), Encoding.UTF8, "application/json")
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

        var httpClient = serviceProvider.GetRequiredService<HttpClient>();
        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Chat stream failed: {await response.Content.ReadAsStringAsync(cancellationToken)}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var assistantResponse = new StringBuilder();

        string line;
        var isReasoningContent = false;
        var isStart = true;
        const string thinkStart = "<think>";
        const string thinkEnd = "</think>";
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

            var delta = chunk.Choices[0].Delta;
            var content = delta.Content;

            // Размышляющие модели по разному отдают размышления
            //
            //           ReasoningContent | Content
            // GLM 4.6         +++        |   ---
            // Kimi 2          +++        | <think>
            // Deepseek R1     ---        | <think>
            //
            // обрабатываем размышления как Z.ai GLM.
            // Все размышления идут в ReasoningContent с пустым Content

            if (delta.ReasoningContent == null && !string.IsNullOrEmpty(content))
            {
                if (!isReasoningContent) // не думаем
                {
                    if (isStart && content.StartsWith(thinkStart))
                    {
                        // начать думать можно только в первом чанке
                        isReasoningContent = true;
                        delta.ReasoningContent = content.Replace(thinkStart, string.Empty);
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
                    if (content.Contains(thinkEnd))
                    {
                        // если закончил думать, то можно в контент добавить часть чанка (актуально для Kimi2)
                        isReasoningContent = false;
                        delta.Content = content.Replace(thinkEnd, string.Empty);
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

    public async Task<ConversationSession> GetOrCreateSessionAsync(string sessionId)
    {
        var sessionList = await GetSessionsListAsync();
        ConversationSession session;
        if (sessionList.Contains(sessionId))
        {
            session = await localStorage.GetItemAsync<ConversationSession>(sessionId) ??
                new ConversationSession
                {
                    Id = sessionId,
                    MaxMessages = Options.MaxMessages
                };
        }
        else
        {
            session = new ConversationSession
            {
                Id = sessionId,
                MaxMessages = Options.MaxMessages
            };

            sessionList.Add(sessionId);
        }

        await SetSessionsListAsync(sessionList);

        return session;
    }

    private async Task<List<string>> GetSessionsListAsync()
        => await localStorage.GetItemAsync<List<string>>("sessionList") ?? [];

    private async Task SetSessionsListAsync(List<string> sessionList)
        => await localStorage.SetItemAsync("sessionList", sessionList);

    public async Task ClearSessionAsync(string sessionId)
    {
        var sessions = await GetSessionsListAsync();
        var index = sessions.FindIndex(s => s == sessionId);
        if (index != -1)
        {
            sessions.RemoveAt(index);
            await SetSessionsListAsync(sessions);
        }
    }
}