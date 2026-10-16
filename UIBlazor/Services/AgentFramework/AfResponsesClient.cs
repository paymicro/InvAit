using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Собственный <see cref="IChatClient"/>-транспорт для OpenAI Responses API —
/// замена SDK ResponsesClient + AsIChatClient моста (Microsoft.Agents.AI.OpenAI/OpenAI SDK),
/// чтобы убрать зависимость от SDK. Протокол: POST {base}/responses — input-массив вместо
/// messages, ПЛОСКИЙ формат tools (type/name/description/parameters — НЕ вложенный function{}
/// как в Chat Completions), SSE-события с полем "type" (output_text.delta, reasoning_*.delta,
/// output_item.*, function_call_arguments.delta, completed/failed/error). Auth через
/// LlmAuthHeader (Bearer / custom key header) + ExtraHeaders без валидации. Транспорт —
/// DI HttpClient (сохраняет DynamicEnvironmentHttpMessageHandler: VS Code proxy, таймауты).
/// </summary>
internal sealed class AfResponsesClient(ConnectionProfile profile, HttpClient httpClient, Func<int>? httpTimeoutSec = null) : IChatClient
{
    /// <summary>
    /// Streaming: POST /responses (stream=true) и разбор SSE-событий в ChatResponseUpdate.
    /// Строки "event: ..." пропускаются — диспатч по полю "type" из data-JSON.
    /// Прогрессивные function_call: output_item.added (начальный) → function_call_arguments.delta
    /// (куски аргументов накапливаются по item_id) → output_item.done (финальный с полным JSON);
    /// response.completed даёт usage + FinishReason (ToolCalls, если в стриме были function_call).
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(messages, options, stream: true);

        using var timeoutCts = httpTimeoutSec is { } timeoutSeconds
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds()))
            : new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var request = BuildRequest(payload);
        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            throw new LlmApiException($"HttpCode: {(int)response.StatusCode} | server failed: {body}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        // item.id → (call_id, name) из output_item.added/done: function_call_arguments.delta несёт
        // только item_id, поэтому call_id и имя вызова берутся из реестра; куски аргументов
        // накапливаются по item_id.
        var callsByItemId = new Dictionary<string, (string CallId, string Name)>();
        var argumentsByItemId = new Dictionary<string, string>();
        var sawFunctionCall = false;

        while (await reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                // "event: ..." и keep-alive комментарии пропускаем — диспатчим по полю "type" в data-JSON
                continue;
            }

            var json = line[5..].Trim();
            if (json == "[DONE]")
            {
                break;
            }

            var evt = JsonUtils.Deserialize<ResponseEventPayload>(json);
            if (evt is null || string.IsNullOrEmpty(evt.Type))
            {
                continue;
            }

            var contents = new List<AIContent>();
            ChatFinishReason? finishReason = null;

            switch (evt.Type)
            {
                case "response.output_text.delta" when !string.IsNullOrEmpty(evt.Delta):
                    contents.Add(new TextContent(evt.Delta));
                    break;

                // Оба ключа: reasoning_summary_text (дефолтные summary) и reasoning_text (raw reasoning)
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    if (!string.IsNullOrEmpty(evt.Delta))
                    {
                        contents.Add(new TextReasoningContent(evt.Delta));
                    }

                    break;

                case "response.output_item.added":
                case "response.output_item.done":
                    if (evt.Item is { } item && item.Type == "function_call")
                    {
                        sawFunctionCall = true;
                        var callId = item.CallId ?? item.Id ?? string.Empty;
                        if (!string.IsNullOrEmpty(item.Id))
                        {
                            callsByItemId[item.Id] = (callId, item.Name ?? string.Empty);
                        }

                        // added — начальный (аргументы ещё пустые), done — финальный с полным JSON
                        contents.Add(new FunctionCallContent(
                            callId,
                            item.Name ?? string.Empty,
                            JsonUtils.DeserializePartialAnonymousType(item.Arguments ?? string.Empty, new Dictionary<string, object?>())));
                    }

                    break;

                case "response.function_call_arguments.delta":
                    if (evt.ItemId is { Length: > 0 } itemId && !string.IsNullOrEmpty(evt.Delta))
                    {
                        var accumulated = (argumentsByItemId.TryGetValue(itemId, out var prev) ? prev : string.Empty) + evt.Delta;
                        argumentsByItemId[itemId] = accumulated;

                        var (partialCallId, partialName) = callsByItemId.TryGetValue(itemId, out var known)
                            ? known
                            : (itemId, string.Empty);

                        // Частичный JSON кусков аргументов посреди стрима не парсится — пустой словарь,
                        // финальный output_item.done даст полный набор.
                        contents.Add(new FunctionCallContent(partialCallId, partialName, JsonUtils.DeserializePartialAnonymousType(accumulated, new Dictionary<string, object?>())));
                    }

                    break;

                case "response.completed":
                case "response.incomplete":
                    if (evt.Response?.Usage is { } usage)
                    {
                        contents.Add(new UsageContent(BuildUsageDetails(usage)));
                    }

                    // Truncated response (incomplete): mark Length so the truncated answer
                    // is not finalized as a normal stop/tool_calls turn.
                    finishReason = evt.Type == "response.incomplete"
                        ? ChatFinishReason.Length
                        : sawFunctionCall ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop;
                    break;

                case "response.failed":
                case "error":
                    var errorMessage = evt.Type == "response.failed"
                        ? evt.Response?.Error?.Message
                        : evt.Message;
                    throw new LlmApiException(!string.IsNullOrWhiteSpace(errorMessage) ? errorMessage : json);

                // Прочие типы (response.created, annotations...) — игнорируем
            }

            // yield только непустых апдейтов (есть Contents или FinishReason)
            if (contents.Count == 0 && finishReason is null)
            {
                continue;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, contents)
            {
                // ModelId: response.completed/response.incomplete несёт response.model — иначе
                // streaming никогда не ставит ModelId (capture.Model остаётся null).
                ModelId = evt.Response?.Model,
                FinishReason = finishReason
            };
        }
    }

    /// <summary>
    /// Non-streaming: тот же payload со stream=false, ответ — единый JSON response:
    /// output[] (message/reasoning/function_call), usage, error.
    /// </summary>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(messages, options, stream: false);

        using var timeoutCts = httpTimeoutSec is { } timeoutSeconds
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds()))
            : new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var request = BuildRequest(payload);
        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, linkedCts.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            throw new LlmApiException($"HttpCode: {(int)response.StatusCode} | server failed: {body}");
        }

        var json = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
        var completion = JsonUtils.Deserialize<ResponsesApiResponse>(json)
            ?? throw new LlmApiException($"HttpCode: {(int)response.StatusCode} | server failed: {json}");

        if (completion.Error is not null)
        {
            throw new LlmApiException(!string.IsNullOrWhiteSpace(completion.Error.Message) ? completion.Error.Message : json);
        }

        var contents = new List<AIContent>();
        var sawFunctionCall = false;

        foreach (var item in completion.Output ?? [])
        {
            switch (item.Type)
            {
                case "message":
                    // content: [{"type":"output_text","text":...}]
                    foreach (var part in item.Content ?? [])
                    {
                        if (part.Type == "output_text" && !string.IsNullOrEmpty(part.Text))
                        {
                            contents.Add(new TextContent(part.Text));
                        }
                    }

                    break;

                case "reasoning":
                    // summary: [{"type":"summary_text","text":...}]
                    foreach (var part in item.Summary ?? [])
                    {
                        if (part.Type == "summary_text" && !string.IsNullOrEmpty(part.Text))
                        {
                            contents.Add(new TextReasoningContent(part.Text));
                        }
                    }

                    break;

                case "function_call":
                    sawFunctionCall = true;
                    contents.Add(new FunctionCallContent(
                        item.CallId ?? string.Empty,
                        item.Name ?? string.Empty,
                        JsonUtils.DeserializePartialAnonymousType(item.Arguments ?? string.Empty, new Dictionary<string, object?>())));
                    break;
            }
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            // ModelId = модель (не id ответа resp_...).
            ModelId = completion.Model ?? string.Empty,
            FinishReason = completion.Status == "incomplete"
                ? ChatFinishReason.Length
                : sawFunctionCall ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Usage = completion.Usage is { } usage ? BuildUsageDetails(usage) : null
        };
    }

    public object? GetService(Type serviceType, object? serviceKey) =>
        serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata(
                "AfResponses",
                Uri.TryCreate(ConnectionProfile.NormalizeApiBase(profile.Endpoint), UriKind.Absolute, out var url) ? url : null,
                profile.Model)
            : null;

    // HttpClient принадлежит DI — здесь не освобождаем
    public void Dispose() { }

    /// <summary>
    /// Payload Responses API: model/input/temperature/stream, max_output_tokens (>= 1000),
    /// merge ExtraPayload (kvp override), tools из ChatOptions в ПЛОСКОМ формате
    /// (type/name/description/parameters — wire-схема из AIFunctionDeclaration.JsonSchema)
    /// + tool_choice=auto.
    /// </summary>
    private Dictionary<string, object> BuildPayload(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = profile.Model,
            ["input"] = ConvertMessages(messages),
            ["temperature"] = options?.Temperature ?? profile.Temperature,
            ["stream"] = stream
        };

        var maxTokens = options?.MaxOutputTokens ?? profile.MaxTokens;
        if (maxTokens >= 1000)
        {
            payload["max_output_tokens"] = maxTokens;
        }

        if (!string.IsNullOrWhiteSpace(profile.ExtraPayload))
        {
            try
            {
                var extra = JsonSerializer.Deserialize<Dictionary<string, object>>(profile.ExtraPayload);
                if (extra is not null)
                {
                    foreach (var kvp in extra)
                    {
                        payload[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (JsonException)
            {
                // Некорректный пользовательский JSON — пропускаем
            }
        }

        if (options?.Tools is { Count: > 0 } tools)
        {
            var wireTools = new List<object>();
            foreach (var tool in tools)
            {
                // AIFunction (invokable) наследуется от AIFunctionDeclaration — оба несут wire-схему
                if (tool is AIFunctionDeclaration declaration)
                {
                    // ПЛОСКИЙ формат Responses API (НЕ вложенный function{} как в Chat Completions)
                    wireTools.Add(new
                    {
                        type = "function",
                        name = declaration.Name,
                        description = declaration.Description,
                        parameters = declaration.JsonSchema,
                        // Legacy parity: strict tool-calling (NativeToolFunction.Strict).
                        strict = true
                    });
                }
            }

            if (wireTools.Count > 0)
            {
                payload["tools"] = wireTools;
                payload["tool_choice"] = "auto";
            }
        }

        return payload;
    }

    /// <summary>
    /// ChatMessage → input-массив Responses API. system/user → {role, content: текст};
    /// assistant: TextContent → {role:assistant, content:[{type:output_text}]}, а каждый
    /// FunctionCallContent — ОТДЕЛЬНЫЙ элемент {type:function_call, call_id, name, arguments};
    /// role tool + FunctionResultContent → {type:function_call_output, call_id, output}.
    /// TextReasoningContent НЕ отправляется обратно (reasoning не ресентится — легаси-парити).
    /// </summary>
    private static List<Dictionary<string, object>> ConvertMessages(IEnumerable<ChatMessage> messages)
    {
        var result = new List<Dictionary<string, object>>();

        foreach (var message in messages)
        {
            var role = message.Role.Value switch
            {
                "system" => "system",
                "user" => "user",
                "assistant" => "assistant",
                "tool" => "tool",
                _ => "user"
            };

            if (role == "tool")
            {
                // FunctionResultContent → function_call_output
                foreach (var content in message.Contents)
                {
                    if (content is FunctionResultContent functionResult)
                    {
                        result.Add(new Dictionary<string, object>
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = functionResult.CallId,
                            ["output"] = functionResult.Result?.ToString() ?? string.Empty
                        });
                    }
                }

                continue;
            }

            if (role == "assistant")
            {
                // TextContent → assistant-элемент с output_text-частями
                var outputText = new List<object>();
                foreach (var textContent in message.Contents.OfType<TextContent>())
                {
                    if (!string.IsNullOrEmpty(textContent.Text))
                    {
                        outputText.Add(new { type = "output_text", text = textContent.Text });
                    }
                }

                if (outputText.Count > 0)
                {
                    result.Add(new Dictionary<string, object>
                    {
                        ["role"] = "assistant",
                        ["content"] = outputText
                    });
                }

                // FunctionCallContent → отдельные function_call-элементы
                foreach (var content in message.Contents)
                {
                    if (content is FunctionCallContent functionCall)
                    {
                        result.Add(new Dictionary<string, object>
                        {
                            ["type"] = "function_call",
                            ["call_id"] = functionCall.CallId,
                            ["name"] = functionCall.Name,
                            ["arguments"] = functionCall.Arguments is { Count: > 0 } args
                                ? JsonSerializer.Serialize(args)
                                : "{}"
                        });
                    }
                }

                continue;
            }

            // system/user: plain-текст (склейка TextContent.Text)
            var text = string.Concat(message.Contents.OfType<TextContent>().Select(c => c.Text));
            result.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = text });
        }

        return result;
    }

    /// <summary>
    /// POST на {base}/responses: NormalizeApiBase добавляет /v1 к bare-базе (легаси-парити —
    /// SDK делал то же самое), auth через LlmAuthHeader (Bearer / custom key header)
    /// + ExtraHeaders без валидации.
    /// </summary>
    private HttpRequestMessage BuildRequest(Dictionary<string, object> payload)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ConnectionProfile.NormalizeApiBase(profile.Endpoint)}/responses")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        LlmAuthHeader.Apply(request, profile);

        foreach (var header in profile.ExtraHeaders)
        {
            if (!string.IsNullOrWhiteSpace(header.Name))
            {
                request.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        return request;
    }

    /// <summary>
    /// usage из Responses API (input_tokens/output_tokens/total_tokens + output_tokens_details)
    /// в UsageDetails.
    /// </summary>
    private static UsageDetails BuildUsageDetails(UsagePayload usage) => new()
    {
        InputTokenCount = usage.InputTokens,
        OutputTokenCount = usage.OutputTokens,
        TotalTokenCount = usage.TotalTokens,
        ReasoningTokenCount = usage.OutputTokensDetails?.ReasoningTokens
    };

    // Wire-модели SSE-событий и финального ответа Responses API. snake_case ключи — через
    // JsonPropertyName, остальные совпадают case-insensitively (JsonUtils.Deserialize).
    private sealed class ResponseEventPayload
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("delta")]
        public string? Delta { get; set; }

        [JsonPropertyName("item_id")]
        public string? ItemId { get; set; }

        // top-level message события type="error"
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("item")]
        public OutputItemPayload? Item { get; set; }

        [JsonPropertyName("response")]
        public ResponsePayload? Response { get; set; }
    }

    private sealed class OutputItemPayload
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("call_id")]
        public string? CallId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public string? Arguments { get; set; }

        // message: [{"type":"output_text","text":...}]
        [JsonPropertyName("content")]
        public List<OutputContentPayload>? Content { get; set; }

        // reasoning: [{"type":"summary_text","text":...}]
        [JsonPropertyName("summary")]
        public List<OutputContentPayload>? Summary { get; set; }
    }

    private sealed class OutputContentPayload
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private sealed class ResponsePayload
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("error")]
        public ErrorPayload? Error { get; set; }

        [JsonPropertyName("usage")]
        public UsagePayload? Usage { get; set; }
    }

    private sealed class ResponsesApiResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("output")]
        public List<OutputItemPayload>? Output { get; set; }

        [JsonPropertyName("usage")]
        public UsagePayload? Usage { get; set; }

        [JsonPropertyName("error")]
        public ErrorPayload? Error { get; set; }
    }

    private sealed class ErrorPayload
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class UsagePayload
    {
        [JsonPropertyName("input_tokens")]
        public long InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public long OutputTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public long TotalTokens { get; set; }

        [JsonPropertyName("output_tokens_details")]
        public OutputTokensDetailsPayload? OutputTokensDetails { get; set; }
    }

    private sealed class OutputTokensDetailsPayload
    {
        [JsonPropertyName("reasoning_tokens")]
        public long? ReasoningTokens { get; set; }
    }
}
