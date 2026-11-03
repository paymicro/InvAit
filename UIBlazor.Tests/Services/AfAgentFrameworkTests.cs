using Microsoft.Extensions.AI;

// Radzen.Blazor (global usings in test infra) also declares ChatMessage.
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Unit tests for the Phase 0 Microsoft Agent Framework adapter
/// (<see cref="AfCompletionsAdapter"/>, <see cref="AfMessageMapper"/>, <see cref="AfToolMapper"/>).
/// </summary>
public class AfAgentFrameworkTests
{
    // ── AfMessageMapper ──────────────────────────────────────────────────────────

    [Fact]
    public void Map_LegacyWireMessages_ProducesChatMessages()
    {
        // Arrange - exactly the WireMessage objects GetFormattedMessages produces
        var system = new WireMessage { Role = ChatMessageRole.System, Content = "system prompt" };
        var user = new WireMessage { Role = ChatMessageRole.User, Content = "hello" };
        var assistant = new WireMessage
        {
            Role = ChatMessageRole.Assistant,
            Content = "partial answer",
            ToolCalls = [new WireToolCall("c1", "read_files", "{\"filePath\":\"a.cs\"}")]
        };
        var tool = new WireMessage { Role = ChatMessageRole.Tool, ToolCallId = "c1", Content = "file data" };

        // Act
        var mapped = AfMessageMapper.Map([system, user, assistant, tool]);

        // Assert
        Assert.Equal(4, mapped.Count);
        Assert.Equal(ChatRole.System, mapped[0].Role);
        Assert.Equal("system prompt", mapped[0].Text);

        Assert.Equal(ChatRole.User, mapped[1].Role);
        Assert.Equal("hello", mapped[1].Text);

        Assert.Equal(ChatRole.Assistant, mapped[2].Role);
        Assert.Equal("partial answer", mapped[2].Text);
        var fcc = Assert.IsType<FunctionCallContent>(mapped[2].Contents.Last());
        Assert.Equal("c1", fcc.CallId);
        Assert.Equal("read_files", fcc.Name);
        Assert.Equal("a.cs", fcc.Arguments?["filePath"]?.ToString());

        Assert.Equal(ChatRole.Tool, mapped[3].Role);
        var frc = Assert.IsType<FunctionResultContent>(mapped[3].Contents.Single());
        Assert.Equal("c1", frc.CallId);
        Assert.Equal("file data", frc.Result);
    }

    [Fact]
    public void Map_AssistantWithoutContentAndToolCalls_OmitsTextContent()
    {
        var assistant = new WireMessage
        {
            Role = ChatMessageRole.Assistant,
            ToolCalls = [new WireToolCall("c9", "bash", "{}")]
        };

        var mapped = AfMessageMapper.Map([assistant]);

        Assert.Single(mapped);
        Assert.Equal(ChatRole.Assistant, mapped[0].Role);
        Assert.All(mapped[0].Contents, c => Assert.IsType<FunctionCallContent>(c));
    }

    [Fact]
    public void Map_InvalidArgumentsJson_DropsArguments()
    {
        var assistant = new WireMessage
        {
            Role = ChatMessageRole.Assistant,
            ToolCalls = [new WireToolCall("cx", "tool", "{bad")]
        };

        var mapped = AfMessageMapper.Map([assistant]);

        var fcc = Assert.IsType<FunctionCallContent>(mapped[0].Contents.Single());
        Assert.Equal("cx", fcc.CallId);
        Assert.Null(fcc.Arguments);
    }

    [Fact]
    public void Map_PartialArgumentsJson_RepairsArguments()
    {
        // Truncated JSON (e.g. arguments cut when the session was saved mid-tool-call):
        // the repair path (same as AfAgentLoop.ParseArgs) must restore what it can —
        // the model sees the partial arguments instead of an empty {}.
        var assistant = new WireMessage
        {
            Role = ChatMessageRole.Assistant,
            ToolCalls = [new WireToolCall("cp", "edit_file", "{\"filePath\":\"a.cs\"")]
        };

        var mapped = AfMessageMapper.Map([assistant]);

        var fcc = Assert.IsType<FunctionCallContent>(mapped[0].Contents.Single());
        Assert.Equal("cp", fcc.CallId);
        Assert.NotNull(fcc.Arguments);
        Assert.Equal("a.cs", fcc.Arguments?["filePath"]?.ToString());
    }

    // ── AfToolMapper ─────────────────────────────────────────────────────────────

    [Fact]
    public void MapTool_NativeDefinition_KeepsNameDescriptionSchema()
    {
        var tool = NewTool();

        var mapped = AfToolMapper.Map([tool]);

        Assert.Single(mapped);
        var decl = Assert.IsAssignableFrom<AIFunctionDeclaration>(mapped[0]);
        Assert.Equal("read_files", decl.Name);
        Assert.Equal("Reads files", decl.Description);

        var schema = decl.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("path", schema.GetProperty("properties").GetProperty("filePath").GetProperty("description").GetString());
        Assert.Contains("filePath", schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    // ── AfChatClientFactory ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("sk-test")]
    public void Create_EmptyOrConfiguredApiKey_DoesNotThrow(string apiKey)
    {
        var profile = new ConnectionProfile
        {
            Endpoint = "https://api.test/v1",
            ApiKey = apiKey,
            Model = "test-model"
        };

        using var client = AfChatClientFactory.Create(profile, new HttpClient());

        Assert.NotNull(client);
    }

    [Fact]
    public void Create_CustomApiKeyHeaderAndExtraHeaders_AppliesPolicy()
    {
        var profile = new ConnectionProfile
        {
            Endpoint = "https://api.test/v1",
            ApiKey = "sk-test",
            ApiKeyHeader = "x-api-key",
            Model = "test-model",
            ExtraHeaders = [new() { Name = "x-custom", Value = "custom-value" }]
        };

        using var client = AfChatClientFactory.Create(profile, new HttpClient());

        // Smoke: client is constructed with the header policy registered (pipeline construction doesn't throw).
        Assert.NotNull(client);
    }

    // ── AfChatClientFactory (ApiProvider.Responses) ─────────────────────────────

    [Fact]
#pragma warning disable OPENAI001 // OpenAI Responses bridge (Microsoft.Extensions.AI.OpenAI)
    public void Create_ResponsesProvider_BuildsResponsesBridge()
    {
        var profile = new ConnectionProfile
        {
            Endpoint = "https://api.test/v1",
            ApiKey = "sk-test",
            Model = "test-model",
            ApiProviderType = ApiProvider.Responses
        };

        using var client = AfChatClientFactory.Create(profile, new HttpClient());

        // Smoke: the Responses branch constructs and returns a chat client whose model
        // (via the wrapped ResponsesClient) matches the profile.
        Assert.NotNull(client);
        Assert.Equal("test-model", client.GetService<ChatClientMetadata>().DefaultModelId);
    }
#pragma warning restore OPENAI001

    private static Tool NewTool()
    {
        return new Tool
        {
            Name = "read_files",
            NativeTool = new NativeToolDefinition
            {
                Function = new NativeToolFunction
                {
                    Name = "read_files",
                    Description = "Reads files",
                    Strict = true,
                    Parameters = new NativeParameters
                    {
                        Type = NativeToolType.Object,
                        Properties = new()
                        {
                            ["filePath"] = new NativePropertyDefinition
                            {
                                Type = NativeToolType.String,
                                Description = "path"
                            }
                        },
                        Required = ["filePath"]
                    }
                }
            },
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult())
        };
    }

    // ── AfCompletionsAdapter (streaming) ────────────────────────────────────────

    [Fact]
    public async Task GetCompletionsAsync_Streams_Text_Reasoning_ToolCalls_Usage()
    {
        // Arrange - mirrors what MEAI yields: text/reasoning chunks while streaming,
        // then one combined update with tool calls, and a final usage update.
        var updates = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                Contents = [new TextReasoningContent(" let me think")]
            },
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                Contents = [new TextContent("answer part")]
            },
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                Contents = [new FunctionCallContent("call_1", "read_files", new Dictionary<string, object?> { ["filePath"] = "a.cs" })],
                FinishReason = ChatFinishReason.ToolCalls
            },
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                Contents = [UsageFrom(100, 500, 600, reasoning: 300)]
            }
        };

        var fake = new FakeChatClient(updates);
        var adapter = CreateAdapter(fake);
        var session = new ConversationSession { Id = "s" };
        var capture = new CompletionsResult();

        // Act
        var deltas = await Collect(adapter.GetCompletionsAsync(
            [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], [], session, capture, TestContext.Current.CancellationToken));

        // Assert - deltas
        Assert.Equal(3, deltas.Count);
        Assert.Equal(" let me think", deltas[0].ReasoningContent);
        Assert.Equal("answer part", deltas[1].Content);
        Assert.NotNull(deltas[2].ToolCalls);
        Assert.Single(deltas[2].ToolCalls);

        // Assert - tool call accumulation
        var toolCall = capture.AccumulatedToolCalls!.Single();
        Assert.Equal("call_1", toolCall.Id);
        Assert.Equal("read_files", toolCall.Function.Name);
        Assert.Equal("{\"filePath\":\"a.cs\"}", toolCall.Function.Arguments);
        Assert.Equal(0, toolCall.Index);

        // Assert - finish reason comes from the tool-call update
        Assert.Equal("tool_calls", capture.FinishReason);

        // Assert - usage: reasoning tokens excluded from session context position
        Assert.Equal(500, capture.CompletionTokens);
        Assert.Equal(300, capture.ReasoningTokens);
        Assert.Equal(200, capture.VisibleCompletionTokens);
        Assert.Equal(300, session.TotalTokens);
        Assert.Equal(600, capture.Usage!.TotalTokens);
    }

    [Fact]
    public async Task GetCompletionsAsync_InlineThinkBlock_MovesToReasoningContent()
    {
        // Kimi/DeepSeek style: the think block arrives inside Content.
        // Tags are constructed from parts so the test stays ASCII-robust.
        var thinkStart = "<" + "think>";
        var thinkEnd = "</" + "think>";

        var updates = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                Contents = [new TextContent(thinkStart + "I will read the file")]
            },
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                Contents = [new TextContent(thinkEnd + "Here is the result")]
            }
        };

        var fake = new FakeChatClient(updates);
        var adapter = CreateAdapter(fake);
        var session = new ConversationSession { Id = "s" };
        var capture = new CompletionsResult();

        var deltas = await Collect(adapter.GetCompletionsAsync(
            [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], [], session, capture, TestContext.Current.CancellationToken));

        Assert.Equal(2, deltas.Count);
        Assert.Equal("I will read the file", deltas[0].ReasoningContent);
        Assert.Equal("Here is the result", deltas[1].Content);
    }

    [Fact]
    public async Task GetCompletionsAsync_NoStream_ObjectsUsageAndFinishReason()
    {
        var message = new ChatMessage(ChatRole.Assistant, [new TextContent("direct answer")]);
        var response = new ChatResponse(message)
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "gpt-test"
        };
        var fake = new FakeChatClient(response);
        var adapter = CreateAdapter(fake, stream: false);
        var session = new ConversationSession { Id = "s" };
        var capture = new CompletionsResult();

        var deltas = await Collect(adapter.GetCompletionsAsync(
            [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], [], session, capture, TestContext.Current.CancellationToken));

        Assert.Single(deltas);
        Assert.Equal("direct answer", deltas[0].Content);
        Assert.Equal("stop", capture.FinishReason);
        Assert.Null(capture.Usage);
    }

    [Fact]
    public async Task GetCompletionsAsync_UsageWithoutReasoningDetails_KeepsStreamingEstimate()
    {
        // Provider does not send reasoning token details → the heuristic estimate
        // accumulated during streaming must be preserved ("12345678" 8 chars → 2 tokens).
        var updates = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null) { Contents = [new TextReasoningContent("12345678")] },
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null) { Contents = [new TextContent("abcd")] },
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null) { Contents = [UsageFrom(100, 100, 200, reasoning: null)] }
        };

        var fake = new FakeChatClient(updates);
        var adapter = CreateAdapter(fake);
        var session = new ConversationSession { Id = "s" };
        var capture = new CompletionsResult();

        // Mid-stream: as soon as only reasoning has arrived, CompletionTokens must grow
        // (it always tracks the FULL generation) — otherwise badges look frozen while thinking.
        await using var enumerator = adapter.GetCompletionsAsync(
            [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], [], session, capture, TestContext.Current.CancellationToken).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.NotNull(enumerator.Current.ReasoningContent);
        Assert.Equal(2, capture.CompletionTokens);
        Assert.Equal(2, capture.ReasoningTokens);

        while (await enumerator.MoveNextAsync())
        {
        }

        // Final usage has no reasoning details → heuristic split is preserved; the usage
        // itself overwrites CompletionTokens (full generation incl. this "abcd" delta).
        Assert.Equal(2, capture.ReasoningTokens);
        Assert.Equal(100, capture.CompletionTokens);
        Assert.Equal(98, capture.VisibleCompletionTokens);
        Assert.Equal(198, session.TotalTokens);
    }

    [Fact]
    public async Task GetCompletionsAsync_ReasoningDetailsPresent_OverrideStreamingEstimate()
    {
        // Provider reports exact reasoning_tokens — they win over the char-based heuristic.
        var updates = new[]
        {
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null) { Contents = [new TextReasoningContent("12345678")] },
            new ChatResponseUpdate(ChatRole.Assistant, (string?)null) { Contents = [UsageFrom(50, 80, 130, reasoning: 77)] }
        };

        var fake = new FakeChatClient(updates);
        var adapter = CreateAdapter(fake);
        var session = new ConversationSession { Id = "s" };
        var capture = new CompletionsResult();

        await Collect(adapter.GetCompletionsAsync(
            [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], [], session, capture, TestContext.Current.CancellationToken));

        Assert.Equal(77, capture.ReasoningTokens);
        Assert.Equal(80, capture.CompletionTokens);
        Assert.Equal(53, session.TotalTokens);
    }

    private static AfCompletionsAdapter CreateAdapter(FakeChatClient fake, bool stream = true)
        => new(
            () => new ConnectionProfile { Endpoint = "https://api.test", ApiKey = "key", Model = "test-model", Stream = stream, Temperature = 0.7f, MaxTokens = 1000 },
            new HttpClient(),
            () => 30,
            (_, _) => fake);

    private static UsageContent UsageFrom(int input, int output, int total, int? reasoning)        => new(new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            TotalTokenCount = total,
            ReasoningTokenCount = reasoning
        });

    private static async Task<List<ChatDelta>> Collect(IAsyncEnumerable<ChatDelta> stream)
    {
        var list = new List<ChatDelta>();
        await foreach (var delta in stream)
        {
            list.Add(delta);
        }
        return list;
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly IAsyncEnumerable<ChatResponseUpdate>? _updates;
        private readonly ChatResponse? _response;

        public FakeChatClient(IEnumerable<ChatResponseUpdate> updates)
            => _updates = Enumerate(updates);

        public FakeChatClient(ChatResponse response)
            => _response = response;

        private static async IAsyncEnumerable<ChatResponseUpdate> Enumerate(IEnumerable<ChatResponseUpdate> updates)
        {
            foreach (var update in updates)
            {
                yield return update;
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CapturedMessages = messages.ToList();
            CapturedOptions = options;
            return Task.FromResult(_response!);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CapturedMessages = messages.ToList();
            CapturedOptions = options;
            return _updates!;
        }

        public List<ChatMessage>? CapturedMessages { get; private set; }
        public ChatOptions? CapturedOptions { get; private set; }

        public object? GetService(Type serviceType, object? serviceKey) => null;
        public void Dispose() { }
    }
}
