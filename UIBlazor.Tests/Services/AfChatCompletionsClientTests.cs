using Microsoft.Extensions.AI;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Integration tests for the own Chat Completions transport (AfChatCompletionsClient built by
/// <see cref="AfChatClientFactory"/> against a WireMock endpoint): reasoning preservation
/// (reasoning_content AND reasoning → TextReasoningContent), progressive tool-call accumulation
/// by index, usage mapping (incl. reasoning_tokens), [DONE] termination, error handling
/// (HTTP 500 and in-stream error chunks → LlmApiException), non-streaming JSON shape and the
/// wire payload (ExtraPayload merge, tools from AIFunctionDeclaration).
/// </summary>
public class AfChatCompletionsClientTests
{
    private static WireMockServer StartSseServer(string sseBody)
    {
        var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithHeader("Cache-Control", "no-cache")
                .WithBody(sseBody));
        return server;
    }

    private static WireMockServer StartJsonServer(string jsonBody, int statusCode = 200)
    {
        var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithHeader("Content-Type", "application/json")
                .WithBody(jsonBody));
        return server;
    }

    private static IChatClient CreateClient(WireMockServer server, Action<ConnectionProfile>? configure = null)
    {
        var profile = new ConnectionProfile
        {
            Endpoint = server.Urls[0],
            ApiKey = "key",
            Model = "test-model",
            Stream = true,
            Temperature = 0.7f,
            MaxTokens = 1000
        };
        configure?.Invoke(profile);
        return AfChatClientFactory.Create(profile, new HttpClient());
    }

    private static string RequestBody(WireMockServer server)
        => server.LogEntries.Select(e => e.RequestMessage.Body).Single();

    private static async Task<List<ChatResponseUpdate>> CollectUpdates(
        IChatClient client, ChatOptions? options = null)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            options,
            TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        return updates;
    }

    // ------------------------------------------------------------------
    // (a) reasoning_content (DeepSeek style) → TextReasoningContent
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Streaming_ReasoningContent_MapsToTextReasoningContent()
    {
        var sse = """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"role":"assistant","reasoning_content":"thinking..."},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"content":"answer"},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{},"finish_reason":"stop"}],"usage":null}

            data: [DONE]

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        Assert.Equal("thinking...", updates[0].Contents.OfType<TextReasoningContent>().Single().Text);
        Assert.Equal("answer", updates[1].Contents.OfType<TextContent>().Single().Text);
        Assert.Equal("stop", updates[2].FinishReason?.Value);
    }

    // ------------------------------------------------------------------
    // (b) reasoning (zai-org GLM style) → TextReasoningContent
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Streaming_GlmReasoningKey_MapsToTextReasoningContent()
    {
        var sse = """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{"role":"assistant","reasoning":"GLM thinking"},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{"content":"answer"},"finish_reason":null}],"usage":null}

            data: [DONE]

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        // The "reasoning" key (not reasoning_content) still lands in TextReasoningContent.
        Assert.Equal("GLM thinking", updates[0].Contents.OfType<TextReasoningContent>().Single().Text);
        Assert.Equal("answer", updates[1].Contents.OfType<TextContent>().Single().Text);
    }

    // ------------------------------------------------------------------
    // (c) fragmented tool_calls → accumulated full arguments
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Streaming_FragmentedToolCalls_AccumulatesFullArguments()
    {
        // chunk1 = id+name+empty args, chunk2/3 = argument pieces (concatenate to
        // {"filePath":"a.cs"}), chunk4 = finish_reason=tool_calls.
        var sse = """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":""}}]},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"file"}}]},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"Path\":\"a.cs\"}"}}]},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{},"finish_reason":"tool_calls"}],"usage":null}

            data: [DONE]

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        // Progressive emission: one FunctionCallContent update per tool-call chunk.
        var callUpdates = updates.Where(u => u.Contents.OfType<FunctionCallContent>().Any()).ToList();
        Assert.Equal(3, callUpdates.Count);

        // Every update keeps the accumulated id/name (fragments carry neither).
        Assert.All(callUpdates, u =>
        {
            var call = u.Contents.OfType<FunctionCallContent>().Single();
            Assert.Equal("call_1", call.CallId);
            Assert.Equal("read_file", call.Name);
        });

        // Chunk 1: accumulated "" — empty template (no arguments yet).
        Assert.Empty(callUpdates[0].Contents.OfType<FunctionCallContent>().Single().Arguments);

        // Chunk 2: accumulated "{\"file" — cut ON A KEY (no value to close): RepairJson
        // cannot salvage it, the partial dictionary stays empty.
        Assert.Empty(callUpdates[1].Contents.OfType<FunctionCallContent>().Single().Arguments);

        // Chunk 3: the concatenation became parseable - full arguments.
        var finalCall = callUpdates[2].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("a.cs", Assert.IsType<JsonElement>(finalCall.Arguments["filePath"]).GetString());

        // Finish reason arrives as its own update.
        Assert.Equal("tool_calls", updates[^1].FinishReason?.Value);
    }

    [Fact(Timeout = 20000)]
    public async Task Streaming_FragmentedToolCalls_CutInsideValue_YieldsPartialDictionary()
    {
        // Fragmentation INSIDE a value (after the colon): RepairJson closes the string →
        // a NON-empty partial dictionary mid-stream (the progressive UI case).
        var sse = """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"filePath\":\"a"}}]},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"index":0,"function":{"arguments":".cs\"}"}}]},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{},"finish_reason":"tool_calls"}],"usage":null}

            data: [DONE]

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        var calls = updates.SelectMany(u => u.Contents.OfType<FunctionCallContent>()).ToList();
        Assert.Equal(2, calls.Count);

        // Chunk 1: accumulated "{\"filePath\":\"a" — cut inside the value: RepairJson closes
        // the string → partial dict with the truncated value.
        var partial = calls[0].Arguments;
        Assert.NotEmpty(partial);
        Assert.Equal("a", Assert.IsType<JsonElement>(partial["filePath"]).GetString());

        // Chunk 2: full arguments.
        Assert.Equal("a.cs", Assert.IsType<JsonElement>(calls[1].Arguments["filePath"]).GetString());
    }

    // ------------------------------------------------------------------
    // (c2) tool_calls with missing index: chunks of DIFFERENT calls must not collapse
    //      into index 0 (args of different calls concatenated before the fix)
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Streaming_ToolCallsWithoutIndex_DifferentIds_StaySeparate()
    {
        // Two calls, NEITHER carries index (some servers omit it): grouping is by id.
        // The old code keyed by index ?? 0 — both calls collapsed into index 0 and their
        // arguments concatenated into one garbled call.
        var sse = """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"filePath\":"}}]},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"id":"call_2","type":"function","function":{"name":"bash","arguments":"{\"command\":"}}]},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"id":"call_1","function":{"arguments":"\"a.cs\"}"}}]},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"id":"call_2","function":{"arguments":"\"ls\"}"}}]},"finish_reason":null}],"usage":null}

            data: [DONE]

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        // Both calls accumulate independently, keyed by id.
        var call1 = updates.SelectMany(u => u.Contents.OfType<FunctionCallContent>())
            .Where(c => c.CallId == "call_1").ToList();
        var call2 = updates.SelectMany(u => u.Contents.OfType<FunctionCallContent>())
            .Where(c => c.CallId == "call_2").ToList();

        Assert.Equal(2, call1.Count);
        Assert.Equal(2, call2.Count);

        // call_1 arguments did NOT pick up call_2 fragments (no cross-contamination).
        var final1 = call1[^1];
        Assert.Equal("a.cs", Assert.IsType<JsonElement>(final1.Arguments["filePath"]).GetString());
        Assert.False(final1.Arguments.ContainsKey("command"),
            "call_1 arguments must not contain call_2 fragments (index-collapse regression)");

        var final2 = call2[^1];
        Assert.Equal("ls", Assert.IsType<JsonElement>(final2.Arguments["command"]).GetString());
        Assert.False(final2.Arguments.ContainsKey("filePath"),
            "call_2 arguments must not contain call_1 fragments (index-collapse regression)");
    }

    // ------------------------------------------------------------------
    // (c3) late id: chunks arrive WITHOUT id first, the id lands in a later chunk —
    //      the index-group must merge into the id (one call, not split into two)
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Streaming_ToolCallsLateId_IndexGroupMergesIntoId()
    {
        // chunk1/2 accumulate args WITHOUT id under index 0; chunk3 carries the id.
        // The old code emitted intermediate updates with a fabricated call_0 id and a
        // separate call_1 group — one call split into two (args scattered across two ids).
        var sse = """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"index":0,"function":{"name":"read_file","arguments":"{\"filePath\":"}}]},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.cs\"}"}}]},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function"}]},"finish_reason":null}],"usage":null}

            data: [DONE]

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        var calls = updates.SelectMany(u => u.Contents.OfType<FunctionCallContent>()).ToList();

        // Chunks without id are NOT emitted mid-stream (a fabricated fallback id would
        // split the call into call_0 + call_1 at the late id). The id-group is emitted
        // progressively after the id arrives.
        Assert.NotEmpty(calls);
        Assert.All(calls, c => Assert.Equal("call_1", c.CallId));

        // The full arguments survived the merge (accumulated BEFORE the id arrived).
        var final = calls[^1];
        Assert.Equal("read_file", final.Name);
        Assert.Equal("a.cs", Assert.IsType<JsonElement>(final.Arguments["filePath"]).GetString());
    }

    // ------------------------------------------------------------------
    // (c4) id never arrives: the index-group is emitted at the END of the stream with
    //      the fallback id — args land in AccumulatedToolCalls as ONE call
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Streaming_ToolCallsIdNeverArrives_EmittedAtStreamEndWithFallbackId()
    {
        var sse = """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"tool_calls":[{"index":0,"function":{"name":"read_file","arguments":"{\"filePath\":\"a.cs\"}"}}]},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{},"finish_reason":"tool_calls"}],"usage":null}

            data: [DONE]

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        var calls = updates.SelectMany(u => u.Contents.OfType<FunctionCallContent>()).ToList();

        // The call is emitted ONCE at the end of the stream (after [DONE]) with the
        // fallback id — no fabricated mid-stream call_0 updates.
        Assert.Single(calls);
        Assert.Equal("call_0", calls[0].CallId);
        Assert.Equal("read_file", calls[0].Name);
        Assert.Equal("a.cs", Assert.IsType<JsonElement>(calls[0].Arguments["filePath"]).GetString());

        // The finish-reason update comes BEFORE the finalized call (stream-end emission).
        var finishUpdateIndex = updates.FindIndex(u => u.FinishReason is not null);
        var callUpdateIndex = updates.FindIndex(u => u.Contents.OfType<FunctionCallContent>().Any());
        Assert.True(callUpdateIndex > finishUpdateIndex,
            "the finalized call must be emitted after the finish-reason update");
    }

    // ------------------------------------------------------------------
    // (d) usage chunk → UsageContent with reasoning_tokens
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Streaming_UsageChunk_MapsReasoningTokenCount()
    {
        var sse = """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"role":"assistant","content":"answer"},"finish_reason":null}],"usage":null}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"completion_tokens_details":{"reasoning_tokens":3}}}

            data: [DONE]

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        // The final usage chunk (no choices) yields a separate UsageContent update.
        var usage = updates.SelectMany(u => u.Contents.OfType<UsageContent>()).Single();
        Assert.Equal(10L, usage.Details.InputTokenCount);
        Assert.Equal(5L, usage.Details.OutputTokenCount);
        Assert.Equal(15L, usage.Details.TotalTokenCount);
        Assert.Equal(3L, usage.Details.ReasoningTokenCount);
    }

    // ------------------------------------------------------------------
    // (e) HTTP 500 → LlmApiException
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Http500_ThrowsLlmApiException()
    {
        using var server = StartJsonServer("server exploded", statusCode: 500);
        using var client = CreateClient(server);

        var exception = await Assert.ThrowsAsync<LlmApiException>(() => CollectUpdates(client));

        Assert.Contains("HttpCode: 500", exception.Message);
        Assert.Contains("server exploded", exception.Message);
    }

    // ------------------------------------------------------------------
    // (f) in-stream error chunk → LlmApiException
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task StreamErrorChunk_ThrowsLlmApiException()
    {
        var sse = """
            data: {"error":{"message":"boom","type":"server_error","param":null,"code":null}}

            data: [DONE]

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var exception = await Assert.ThrowsAsync<LlmApiException>(() => CollectUpdates(client));

        Assert.Contains("boom", exception.Message);
    }

    // ------------------------------------------------------------------
    // (f2) error field nested in an otherwise normal chunk → LlmApiException
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task StreamNestedErrorChunk_ThrowsLlmApiException()
    {
        // Some relays nest the error field inside an otherwise normal chunk:
        // the {"error" prefix check misses it — the error must still surface.
        var sse = """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"role":"assistant","content":"partial"},"finish_reason":null}],"usage":null,"error":{"message":"relay exploded","type":"server_error"}}

            data: [DONE]

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var exception = await Assert.ThrowsAsync<LlmApiException>(() => CollectUpdates(client));

        Assert.Contains("relay exploded", exception.Message);
    }

    [Fact(Timeout = 20000)]
    public async Task StreamNestedErrorString_ThrowsLlmApiException()
    {
        // The nested error can also be a plain string (not an object).
        var sse = """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"content":"partial"},"finish_reason":null}],"usage":null,"error":"string error"}

            data: [DONE]

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var exception = await Assert.ThrowsAsync<LlmApiException>(() => CollectUpdates(client));

        Assert.Contains("string error", exception.Message);
    }

    // ------------------------------------------------------------------
    // (g) [DONE] terminates the stream
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task DoneMarker_TerminatesStream()
    {
        // An error chunk AFTER [DONE]: if the marker did not break the loop,
        // the transport would throw - the absence of the exception proves termination.
        var sse = """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"message":null,"delta":{"role":"assistant","content":"hi there"},"finish_reason":null}],"usage":null}

            data: [DONE]

            data: {"error":{"message":"must not be read"}}

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        var update = Assert.Single(updates);
        Assert.Equal("hi there", update.Contents.OfType<TextContent>().Single().Text);
    }

    // ------------------------------------------------------------------
    // (h) non-streaming JSON with reasoning → TextReasoningContent + FinishReason
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task NonStreaming_Reasoning_MapsToTextReasoningContent_WithFinishReason()
    {
        const string json = """
            {
              "id": "chatcmpl-1",
              "object": "chat.completion",
              "created": 1,
              "model": "zai-org/GLM-5",
              "choices": [
                {
                  "index": 0,
                  "message": {
                    "role": "assistant",
                    "content": "answer",
                    "reasoning": "REASONING_NONSTREAM"
                  },
                  "finish_reason": "stop"
                }
              ],
              "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2}
            }
            """;
        using var server = StartJsonServer(json);
        using var client = CreateClient(server, p => p.Stream = false);

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        var message = Assert.Single(response.Messages);
        Assert.Equal("answer", Assert.Single(message.Contents.OfType<TextContent>()).Text);
        Assert.Equal("REASONING_NONSTREAM", Assert.Single(message.Contents.OfType<TextReasoningContent>()).Text);
        Assert.Equal("stop", response.FinishReason?.Value);
    }

    // ------------------------------------------------------------------
    // (h2) GetService: ChatClientMetadata from BOTH own transports (consistent)
    // ------------------------------------------------------------------

    [Fact]
    public void GetService_ChatClientMetadata_ReturnsProviderAndModel()
    {
        using var server = StartSseServer("data: [DONE]\n\n");
        using var client = CreateClient(server);

        var metadata = client.GetService<ChatClientMetadata>();

        Assert.NotNull(metadata);
        Assert.Equal("test-model", metadata.DefaultModelId);
        Assert.Equal("AfChatCompletions", metadata.ProviderName);
    }

    // ------------------------------------------------------------------
    // (i) payload contains ExtraPayload keys (merge + override)
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Payload_IncludesExtraPayloadKeys_WithOverride()
    {
        using var server = StartSseServer("data: [DONE]\n\n");
        using var client = CreateClient(server, p => p.ExtraPayload = """{"top_k": 40, "temperature": 0.1}""");

        await CollectUpdates(client);

        using var body = JsonDocument.Parse(RequestBody(server));
        var root = body.RootElement;

        // Extra keys are merged into the payload.
        Assert.Equal(40, root.GetProperty("top_k").GetInt32());

        // ExtraPayload overrides the profile value (kvp override merge).
        Assert.Equal(0.1, root.GetProperty("temperature").GetDouble());

        // Base payload keys survive the merge.
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
    }

    // ------------------------------------------------------------------
    // (j) tools in payload: name/description/parameters from AIFunctionDeclaration
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Payload_IncludesTools_FromDeclaration()
    {
        using var server = StartSseServer("data: [DONE]\n\n");
        using var client = CreateClient(server);

        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { filePath = new { type = "string" } },
            required = new[] { "filePath" }
        });
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.CreateDeclaration("read_file", "Read a file from disk", schema)]
        };

        await CollectUpdates(client, options);

        using var body = JsonDocument.Parse(RequestBody(server));
        var root = body.RootElement;

        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());

        var tool = root.GetProperty("tools").EnumerateArray().Single();
        Assert.Equal("function", tool.GetProperty("type").GetString());
        var function = tool.GetProperty("function");
        Assert.Equal("read_file", function.GetProperty("name").GetString());
        Assert.Equal("Read a file from disk", function.GetProperty("description").GetString());
        Assert.Equal("filePath", function.GetProperty("parameters").GetProperty("properties").EnumerateObject().Single().Name);
    }
}
