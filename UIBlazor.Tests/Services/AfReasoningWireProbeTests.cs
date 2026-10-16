using Microsoft.Extensions.AI;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Regression tests for reasoning preservation in the own Chat Completions transport
/// (<see cref="AfChatClientFactory"/> → AfChatCompletionsClient): the provider-specific
/// "reasoning_content" (DeepSeek style) and "reasoning" (zai-org GLM style) fields must map
/// into <see cref="TextReasoningContent"/> — both per streaming delta chunk and in the
/// non-streaming chat.completion shape. The former OpenAI SDK bridge dropped the "reasoning"
/// delta field entirely (documented by the probe tests this file replaces).
/// </summary>
public class AfReasoningWireProbeTests
{
    // Chunk style copied from AfSubAgentOrchestratorCompressionTests.SseResponse:
    // chunk 1 = reasoning_content, chunk 2 = reasoning, chunk 3 = stop.
    private const string SseResponse = """
        data: {"id":"d9d528e22108450d96716ce5f36fb2ea","object":"chat.completion.chunk","created":1773130988,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{"role":"assistant","reasoning_content":"REASONING_FROM_REASONING_CONTENT"},"finish_reason":null}],"usage":null}

        data: {"id":"d9d528e22108450d96716ce5f36fb2ea","object":"chat.completion.chunk","created":1773130988,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{"reasoning":"REASONING_FROM_REASONING"},"finish_reason":null}],"usage":null}

        data: {"id":"d9d528e22108450d96716ce5f36fb2ea","object":"chat.completion.chunk","created":1773130988,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{},"finish_reason":"stop"}],"usage":null}

        data: [DONE]

        """;

    private const string NonStreamingResponse = """
        {
          "id": "chatcmpl-probe-1",
          "object": "chat.completion",
          "created": 1773130988,
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

    private static WireMockServer StartSseServer()
    {
        var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithHeader("Cache-Control", "no-cache")
                .WithBody(SseResponse));
        return server;
    }

    private static WireMockServer StartJsonServer()
    {
        var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(NonStreamingResponse));
        return server;
    }

    // ------------------------------------------------------------------
    // Streaming path (chat.completion.chunk SSE)
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Streaming_ReasoningFields_MapToTextReasoningContent()
    {
        using var server = StartSseServer();
        using var httpClient = new HttpClient();
        IChatClient client = AfChatClientFactory.Create(
            new ConnectionProfile
            {
                Endpoint = server.Urls[0],
                ApiKey = "key",
                Model = "test-model",
                Stream = true,
                Temperature = 0.7f,
                MaxTokens = 1000
            },
            httpClient);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        // Both provider-specific reasoning fields land in TextReasoningContent,
        // one update per chunk (progressive streaming).
        var reasoningTexts = updates
            .SelectMany(u => u.Contents.OfType<TextReasoningContent>())
            .Select(r => r.Text)
            .ToList();
        Assert.Equal(2, reasoningTexts.Count);
        Assert.Contains("REASONING_FROM_REASONING_CONTENT", reasoningTexts);
        Assert.Contains("REASONING_FROM_REASONING", reasoningTexts);

        // The trailing finish_reason=stop chunk arrives as its own update.
        Assert.Contains(updates, u => u.FinishReason?.Value == "stop");
    }

    // ------------------------------------------------------------------
    // Non-streaming path (single chat.completion JSON)
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task NonStreaming_ReasoningField_MapsToTextReasoningContent()
    {
        using var server = StartJsonServer();
        using var httpClient = new HttpClient();
        IChatClient client = AfChatClientFactory.Create(
            new ConnectionProfile
            {
                Endpoint = server.Urls[0],
                ApiKey = "key",
                Model = "test-model",
                Stream = false,
                Temperature = 0.7f,
                MaxTokens = 1000
            },
            httpClient);

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        var message = Assert.Single(response.Messages);
        Assert.Equal("answer", Assert.Single(message.Contents.OfType<TextContent>()).Text);

        // GLM-style "reasoning" key in the non-streaming message.
        var reasoning = Assert.Single(message.Contents.OfType<TextReasoningContent>());
        Assert.Equal("REASONING_NONSTREAM", reasoning.Text);

        Assert.Equal("stop", response.FinishReason?.Value);
    }
}
