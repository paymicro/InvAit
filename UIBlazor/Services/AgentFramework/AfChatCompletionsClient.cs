using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Собственный <see cref="IChatClient"/>-транспорт для OpenAI-совместимого Chat Completions API —
/// замена OpenAI SDK (AsIChatClient): SDK терял поле reasoning (reasoning_content/reasoning
/// не маппились в TextReasoningContent) и буферизовал tool_calls до конца ответа.
/// SSE-парсинг повторяет легаси-парсер из старого ChatService (до 30b28c83): payload,
/// построчное чтение data:, reasoning, tool_calls по index, usage, [DONE], error-чанки, non-streaming.
/// Транспорт — DI HttpClient (сохраняет DynamicEnvironmentHttpMessageHandler: VS Code proxy, таймауты).
/// </summary>
internal sealed class AfChatCompletionsClient(ConnectionProfile profile, HttpClient httpClient, Func<int>? httpTimeoutSec = null) : IChatClient
{
    /// <summary>
    /// Streaming: POST /chat/completions (stream=true, stream_options.include_usage) и разбор
    /// SSE-чанков в ChatResponseUpdate. reasoning_content/reasoning маппятся в TextReasoningContent,
    /// tool_calls накапливаются прогрессивно по index, usage — отдельным финальным чанком.
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

        // Прогрессивное накопление tool_calls (легаси-парити): id/name присваиваются
        // если непустые, arguments дописываются; финальный чанк (finish_reason) даёт полный JSON.
        var toolAccumulator = new ToolCallAccumulator();

        while (await reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var json = line[5..].Trim();
            if (json == "[DONE]")
            {
                break;
            }

            if (json.StartsWith("{\"error\"", StringComparison.Ordinal))
            {
                throw new LlmApiException(json);
            }

            var chunk = JsonUtils.Deserialize<ChunkPayload>(json);
            if (chunk is null)
            {
                continue;
            }

            // Error field nested in an otherwise normal chunk (some relays): the {"error"
            // prefix check above misses it — the error would be silently swallowed.
            if (chunk.Error is { } chunkError)
            {
                var nestedMessage = chunkError.ValueKind switch
                {
                    JsonValueKind.String => chunkError.GetString(),
                    JsonValueKind.Object => chunkError.TryGetProperty("message", out var m)
                            && m.ValueKind == JsonValueKind.String
                        ? m.GetString()
                        : chunkError.GetRawText(),
                    _ => null
                };

                if (!string.IsNullOrWhiteSpace(nestedMessage))
                {
                    throw new LlmApiException(nestedMessage);
                }
            }

            var contents = new List<AIContent>();
            ChatFinishReason? finishReason = null;
            var role = ChatRole.Assistant;

            if (chunk.Choices is { Count: > 0 } && chunk.Choices[0] is { } choice)
            {
                finishReason = MapFinishReason(choice.FinishReason);

                if (choice.Delta?.Role is { Length: > 0 } deltaRole)
                {
                    role = new ChatRole(deltaRole);
                }

                MapDelta(choice.Delta, contents, toolAccumulator);
            }

            if (chunk.Usage is { } usage)
            {
                // Финальный чанк без choices (stream_options.include_usage)
                contents.Add(new UsageContent(BuildUsageDetails(usage)));
            }

            // yield только непустых апдейтов (есть Contents или FinishReason)
            if (contents.Count == 0 && finishReason is null)
            {
                continue;
            }

            yield return new ChatResponseUpdate(role, contents)
            {
                // MessageId = id completion'а: один id на все чанки ответа, новый — на следующий
                // вызов модели в цикле (AfDeltaMapper детектит смену хода по MessageId).
                MessageId = chunk.Id,
                ModelId = chunk.Model,
                FinishReason = finishReason
            };
        }

        // Финализация: куски без id копились под index (прогрессивные апдейты не отдавались —
        // выдуманный fallback id расщепил бы вызов на два при позднем id). Если id так и не
        // пришёл, вызов отдаётся здесь с fallback id — args попадают в capture ОДНИМ вызовом.
        foreach (var (index, name, args) in toolAccumulator.DrainUnresolved())
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent(
                    $"call_{index}",
                    name ?? string.Empty,
                    JsonUtils.DeserializePartialAnonymousType(args, new Dictionary<string, object?>()))
            });
        }
    }

    /// <summary>
    /// Non-streaming: тот же payload со stream=false, ответ — единый JSON chat.completion.
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
        var completion = JsonUtils.Deserialize<CompletionPayload>(json)
            ?? throw new LlmApiException($"HttpCode: {(int)response.StatusCode} | server failed: {json}");

        var choice = completion.Choices is { Count: > 0 } ? completion.Choices[0] : null;
        var contents = new List<AIContent>();

        if (choice?.Message is { } message)
        {
            if (!string.IsNullOrEmpty(message.Content))
            {
                contents.Add(new TextContent(message.Content));
            }

            // ОБА ключа: reasoning_content (DeepSeek) и reasoning (zai-org GLM)
            var reasoning = message.ReasoningContent ?? message.Reasoning;
            if (!string.IsNullOrEmpty(reasoning))
            {
                contents.Add(new TextReasoningContent(reasoning));
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    var index = toolCall.Index ?? 0;
                    contents.Add(new FunctionCallContent(
                        toolCall.Id ?? $"call_{index}",
                        toolCall.Function?.Name ?? string.Empty,
                        JsonUtils.DeserializePartialAnonymousType(toolCall.Function?.Arguments ?? string.Empty, new Dictionary<string, object?>())));
                }
            }
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = completion.Model,
            FinishReason = MapFinishReason(choice?.FinishReason),
            Usage = completion.Usage is { } usage ? BuildUsageDetails(usage) : null
        };
    }

    public object? GetService(Type serviceType, object? serviceKey) =>
        serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata(
                "AfChatCompletions",
                Uri.TryCreate(ConnectionProfile.NormalizeApiBase(profile.Endpoint), UriKind.Absolute, out var url) ? url : null,
                profile.Model)
            : null;

    // HttpClient принадлежит DI — здесь не освобождаем
    public void Dispose() { }

    /// <summary>
    /// Payload Chat Completions (легаси-формат): model/messages/temperature/stream,
    /// max_tokens + max_completion_tokens (>= 1000), merge ExtraPayload (kvp override),
    /// tools из ChatOptions (wire-схема из AIFunctionDeclaration.JsonSchema) + tool_choice=auto.
    /// </summary>
    private Dictionary<string, object> BuildPayload(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = profile.Model,
            ["messages"] = ConvertMessages(messages),
            ["temperature"] = options?.Temperature ?? profile.Temperature,
            ["stream"] = stream
        };

        if (stream)
        {
            // usage приходит отдельным финальным чанком без choices
            payload["stream_options"] = new { include_usage = true };
        }

        var maxTokens = options?.MaxOutputTokens ?? profile.MaxTokens;
        if (maxTokens >= 1000)
        {
            payload["max_tokens"] = maxTokens;
            payload["max_completion_tokens"] = maxTokens;
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
                    wireTools.Add(new
                    {
                        type = "function",
                        function = new
                        {
                            name = declaration.Name,
                            description = declaration.Description,
                            parameters = declaration.JsonSchema,
                            // Legacy parity: strict tool-calling (NativeToolFunction.Strict).
                            strict = true
                        }
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
    /// ChatMessage → wire-сообщения Chat Completions. TextReasoningContent НЕ отправляется обратно
    /// (reasoning не ресентится — легаси-парити); assistant + FunctionCallContent → tool_calls;
    /// role tool + FunctionResultContent → {role:tool, tool_call_id, content}.
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

            var text = string.Concat(message.Contents.OfType<TextContent>().Select(c => c.Text));
            var toolCalls = new List<object>();
            string? toolCallId = null;
            string toolResult = string.Empty;

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent functionCall:
                        toolCalls.Add(new
                        {
                            id = functionCall.CallId,
                            type = "function",
                            function = new
                            {
                                name = functionCall.Name,
                                arguments = functionCall.Arguments is { Count: > 0 } args
                                    ? JsonSerializer.Serialize(args)
                                    : "{}"
                            }
                        });
                        break;

                    case FunctionResultContent functionResult when role == "tool":
                        toolCallId = functionResult.CallId;
                        toolResult = functionResult.Result?.ToString() ?? string.Empty;
                        break;
                }
            }

            var wire = new Dictionary<string, object> { ["role"] = role };

            if (role == "tool")
            {
                wire["tool_call_id"] = toolCallId ?? string.Empty;
                wire["content"] = toolResult;
            }
            else if (toolCalls.Count > 0)
            {
                wire["tool_calls"] = toolCalls;
                if (text.Length > 0)
                {
                    wire["content"] = text;
                }
            }
            else
            {
                wire["content"] = text;
            }

            result.Add(wire);
        }

        return result;
    }

    /// <summary>
    /// POST на {base}/chat/completions: NormalizeApiBase добавляет /v1 к bare-базе
    /// (легаси-парити — SDK делал то же самое), auth через LlmAuthHeader
    /// (Bearer / custom key header) + ExtraHeaders без валидации.
    /// </summary>
    private HttpRequestMessage BuildRequest(Dictionary<string, object> payload)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ConnectionProfile.NormalizeApiBase(profile.Endpoint)}/chat/completions")
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
    /// Разбор delta одного чанка: content, reasoning (оба ключа) и прогрессивные tool_calls —
    /// на каждый чанк с tool_calls отдаётся FunctionCallContent с накопленным вызовом.
    /// </summary>
    private static void MapDelta(
        ChunkDelta? delta,
        List<AIContent> contents,
        ToolCallAccumulator toolAccumulator)
    {
        if (delta is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(delta.Content))
        {
            contents.Add(new TextContent(delta.Content));
        }

        // ОБА ключа: reasoning_content (DeepSeek) и reasoning (zai-org GLM)
        var reasoning = delta.ReasoningContent ?? delta.Reasoning;
        if (!string.IsNullOrEmpty(reasoning))
        {
            contents.Add(new TextReasoningContent(reasoning));
        }

        if (delta.ToolCalls is not { Count: > 0 })
        {
            return;
        }

        foreach (var toolCall in delta.ToolCalls)
        {
            var (callId, callType, name, arguments) = toolAccumulator.Accumulate(
                toolCall.Index ?? 0,
                toolCall.Id,
                toolCall.Type,
                toolCall.Function?.Name,
                toolCall.Function?.Arguments ?? string.Empty);

            // Аргументы отдаются прогрессивно: накопленный-so-far JSON парсится, если уже
            // валиден (RepairJson больше не нужен — UI получает валидный JSON),
            // невалидный частичный → пустой словарь, финальный чанк даст полный набор.
            // Кусок без id и с ещё неизвестным id записи НЕ отдаётся: выдуманный fallback id
            // расщепил бы вызов на два при позднем id (промежуточные call_0 + финальный call_1).
            if (callId is null)
            {
                continue;
            }

            contents.Add(new FunctionCallContent(
                callId,
                name ?? string.Empty,
                JsonUtils.DeserializePartialAnonymousType(arguments, new Dictionary<string, object?>())));
        }
    }

    /// <summary>
    /// Прогрессивный аккумулятор tool_calls одного ответа. Куски группируются по id вызова,
    /// если кусок его несёт, иначе по index — куски без index разных вызовов больше не
    /// схлопываются в index 0 (args разных вызовов конкатенировались). Поздний id (первые
    /// куски без id) присоединяет index-группу к id-группе — один вызов не расщепляется
    /// на два с fallback-id.
    /// </summary>
    private sealed class ToolCallAccumulator
    {
        private sealed class Entry
        {
            public string? Id;
            public string? Type;
            public string? Name;
            public string Args = string.Empty;
        }

        // Записи по id (куски с id приходят при любом index) + отображение index → запись
        // (куски без id группируются по index).
        private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Entry> _byIndex = [];

        public (string? Id, string? Type, string? Name, string Args) Accumulate(
            int index, string? id, string? type, string? name, string args)
        {
            var entry = ResolveEntry(index, id);

            if (!string.IsNullOrEmpty(id))
            {
                entry.Id = id;
            }

            if (!string.IsNullOrEmpty(type))
            {
                entry.Type = type;
            }

            if (!string.IsNullOrEmpty(name))
            {
                entry.Name = name;
            }

            entry.Args += args;
            return (entry.Id, entry.Type, entry.Name, entry.Args);
        }

        private Entry ResolveEntry(int index, string? id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                if (_byIndex.TryGetValue(index, out var indexed))
                {
                    if (string.IsNullOrEmpty(indexed.Id))
                    {
                        // Поздний id: куски этого вызова копились без id под этим index —
                        // это тот же вызов: присваиваем id и регистрируем в _byId,
                        // чтобы последующие куски с этим id нашли запись напрямую.
                        indexed.Id = id;
                        _byId[id] = indexed;
                        return indexed;
                    }

                    if (indexed.Id != id)
                    {
                        // Новый вызов с тем же index (сервер не шлёт index): последний wins —
                        // продолжения без id пойдут в текущий вызов.
                        var fresh = GetOrCreate(id);
                        _byIndex[index] = fresh;
                        return fresh;
                    }

                    return indexed;
                }

                _byIndex[index] = GetOrCreate(id);
                return _byIndex[index];
            }

            if (_byIndex.TryGetValue(index, out var entry))
            {
                return entry;
            }

            var created = new Entry();
            _byIndex[index] = created;
            return created;
        }

        private Entry GetOrCreate(string id)
            => _byId.TryGetValue(id, out var entry) ? entry : _byId[id] = new Entry();

        /// <summary>
        /// Записи, чей id так и не пришёл за стрим (сервер не шлёт id): отдаются с fallback
        /// id в конце стрима (см. финализацию в <see cref="GetStreamingResponseAsync"/>).
        /// Порядок — по index (OrderBy): порядок итерации Dictionary контрактом не гарантирован,
        /// а он определяет порядок FunctionCallContent → порядок исполнения тулзов агентным циклом.
        /// </summary>
        public IEnumerable<(int Index, string? Name, string Args)> DrainUnresolved()
        {
            foreach (var (index, entry) in _byIndex.OrderBy(kv => kv.Key))
            {
                if (string.IsNullOrEmpty(entry.Id))
                {
                    yield return (index, entry.Name, entry.Args);
                }
            }
        }
    }

    /// <summary>
    /// finish_reason из wire ("stop"/"tool_calls"/"length") в ChatFinishReason.
    /// </summary>
    private static ChatFinishReason? MapFinishReason(string? finishReason) => finishReason switch
    {
        "stop" => ChatFinishReason.Stop,
        "tool_calls" => ChatFinishReason.ToolCalls,
        "length" => ChatFinishReason.Length,
        "content_filter" => ChatFinishReason.ContentFilter,
        null or "" => null,
        _ => new ChatFinishReason(finishReason)
    };

    private static UsageDetails BuildUsageDetails(ChunkUsage usage) => new()
    {
        InputTokenCount = usage.PromptTokens,
        OutputTokenCount = usage.CompletionTokens,
        TotalTokenCount = usage.TotalTokens,
        ReasoningTokenCount = usage.CompletionTokensDetails?.ReasoningTokens
    };

    // Wire-модели SSE-чанков и финального ответа (легаси-парсер). snake_case ключи — через
    // JsonPropertyName, остальные совпадают case-insensitively (JsonUtils.Deserialize).
    private sealed class ChunkPayload
    {
        public string? Id { get; set; }

        public string? Model { get; set; }

        public List<ChunkChoice>? Choices { get; set; }

        public ChunkUsage? Usage { get; set; }

        // Error nested in an otherwise normal chunk (some relays): string or {message}.
        [JsonPropertyName("error")]
        public JsonElement? Error { get; set; }
    }

    private sealed class ChunkChoice
    {
        public ChunkDelta? Delta { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class ChunkDelta
    {
        public string? Role { get; set; }

        public string? Content { get; set; }

        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; set; }

        // zai-org GLM: reasoning приходит в поле "reasoning"
        public string? Reasoning { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<ChunkToolCall>? ToolCalls { get; set; }
    }

    private sealed class ChunkToolCall
    {
        public int? Index { get; set; }

        public string? Id { get; set; }

        public string? Type { get; set; }

        public ChunkFunction? Function { get; set; }
    }

    private sealed class ChunkFunction
    {
        public string? Name { get; set; }

        public string? Arguments { get; set; }
    }

    private sealed class ChunkUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public long PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public long CompletionTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public long TotalTokens { get; set; }

        [JsonPropertyName("completion_tokens_details")]
        public ChunkTokensDetails? CompletionTokensDetails { get; set; }
    }

    private sealed class ChunkTokensDetails
    {
        [JsonPropertyName("reasoning_tokens")]
        public long? ReasoningTokens { get; set; }
    }

    private sealed class CompletionPayload
    {
        public string? Id { get; set; }

        public string? Model { get; set; }

        public List<CompletionChoice>? Choices { get; set; }

        public ChunkUsage? Usage { get; set; }
    }

    private sealed class CompletionChoice
    {
        public CompletionMessage? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class CompletionMessage
    {
        public string? Role { get; set; }

        public string? Content { get; set; }

        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; set; }

        public string? Reasoning { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<ChunkToolCall>? ToolCalls { get; set; }
    }
}
