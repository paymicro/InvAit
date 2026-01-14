using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Radzen;
using UIBlazor.Models;
using UIBlazor.Options;

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

    public async IAsyncEnumerable<string> GetCompletionsAsync(string userInput,
        string sessionId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            throw new ArgumentException("User input cannot be null or empty.", nameof(userInput));
        }

        // Get or create session
        var session = await GetOrCreateSessionAsync(sessionId);

        // Add user message to conversation history
        session.AddMessage("user", userInput);

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

            var content = ParseStreamingResponse(json);
            if (!string.IsNullOrEmpty(content))
            {
                assistantResponse.Append(content);
                yield return content;
            }
        }

        // Add assistant response to conversation history
        if (assistantResponse.Length > 0)
        {
            session.AddMessage("assistant", assistantResponse.ToString());
        }
    }

    public List<AiTool> ParseToolBlock(string content)
    {
        var result = new List<AiTool>();

        var clearContent = RemoveThinkBlock(content);

        // tool calls inside
        var callRegex = new Regex(
            @"<\|tool_call_begin\|>\s*functions\.(\w+):(\d+)\s*<\|tool_call_argument_begin\|>\s*(.*?)\s*<\|tool_call_end\|>",
            RegexOptions.Singleline);

        foreach (Match callMatch in callRegex.Matches(clearContent))
        {
            var toolName = callMatch.Groups[1].Value;
            var callId = callMatch.Groups[2].Value;
            var jsonArgs = callMatch.Groups[3].Value;

            result.Add(new AiTool
            {
                Type = "function",
                Id = callId,
                Index = result.Count,
                Function = new AiToolToCall
                {
                    Name = toolName,
                    Arguments = jsonArgs
                }
            });
        }

        return result;
    }

    public async Task<ConversationSession> GetOrCreateSessionAsync(string sessionId = null)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            sessionId = $"{DateTime.Now:D}";
        }

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

    private static string ParseStreamingResponse(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var firstChoice = choices[0];

            if (!firstChoice.TryGetProperty("delta", out var delta))
            {
                return string.Empty;
            }

            if (delta.TryGetProperty("content", out var contentElement))
            {
                return contentElement.GetString() ?? string.Empty;
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private string RemoveThinkBlock(string input)
        => Regex.Replace(input, @"<think>[\s\S]*?</think>", "", RegexOptions.Singleline);
}