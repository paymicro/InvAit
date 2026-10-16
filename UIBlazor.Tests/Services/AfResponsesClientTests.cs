using Microsoft.Extensions.AI;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Integration tests for the own <see cref="UIBlazor.Services.AgentFramework.AfResponsesClient"/>
/// transport (OpenAI Responses API, no SDK). Pins the wire-level behavior:
/// - reasoning summary deltas map to TextReasoningContent;
/// - function_call arguments arrive as PROGRESSIVE partial deltas (output_item.added →
///   function_call_arguments.delta per chunk → output_item.done with the full JSON) —
///   each chunk yields a FunctionCallContent whose Arguments dictionary holds the
///   repaired-so-far partial JSON, the final one holds the complete arguments;
/// - usage from response.completed (incl. reasoning tokens);
/// - non-streaming mapping (output_text / reasoning summary / function_call).
/// </summary>
public class AfResponsesClientTests
{
    private static WireMockServer StartSseServer(string body)
    {
        var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/v1/responses").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithHeader("Cache-Control", "no-cache")
                .WithBody(body));
        return server;
    }

    private static IChatClient CreateClient(WireMockServer server, bool stream = true)
        => UIBlazor.Services.AgentFramework.AfChatClientFactory.Create(
            new UIBlazor.Models.ConnectionProfile
            {
                Endpoint = server.Urls[0],
                ApiKey = "key",
                Model = "test-model",
                ApiProviderType = UIBlazor.Constants.ApiProvider.Responses,
                Stream = stream
            },
            new HttpClient());

    private static async Task<List<ChatResponseUpdate>> CollectUpdates(IChatClient client)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }
        return updates;
    }

    // ------------------------------------------------------------------
    // (a) reasoning summary deltas → TextReasoningContent
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Streaming_ReasoningSummaryDelta_MapsToTextReasoningContent()
    {
        var sse = """
            event: response.output_text.delta

            data: {"type":"response.output_text.delta","delta":"answer"}

            event: response.reasoning_summary_text.delta

            data: {"type":"response.reasoning_summary_text.delta","delta":"thinking hard"}

            event: response.completed

            data: {"type":"response.completed","response":{"id":"resp_1","usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}}}

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        var reasoning = updates
            .SelectMany(u => u.Contents)
            .OfType<TextReasoningContent>()
            .ToList();
        Assert.Single(reasoning);
        Assert.Equal("thinking hard", reasoning.Single().Text);
    }

    // ------------------------------------------------------------------
    // (b) progressive function_call arguments: partial dictionaries per chunk
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Streaming_FunctionCallArguments_ProgressivePartialDeltas()
    {
        // The wire carries the arguments as fragments: added (empty) →
        // two delta pieces → done with the complete JSON.
        var sse = """
            event: response.output_item.added

            data: {"type":"response.output_item.added","item":{"type":"function_call","id":"item_1","call_id":"call_1","name":"read_file","arguments":""}}

            event: response.function_call_arguments.delta

            data: {"type":"response.function_call_arguments.delta","item_id":"item_1","delta":"{\"file"}

            event: response.function_call_arguments.delta

            data: {"type":"response.function_call_arguments.delta","item_id":"item_1","delta":"Path\":\"a.cs\"}"}

            event: response.output_item.done

            data: {"type":"response.output_item.done","item":{"type":"function_call","id":"item_1","call_id":"call_1","name":"read_file","arguments":"{\"filePath\":\"a.cs\"}"}}

            event: response.completed

            data: {"type":"response.completed","response":{"id":"resp_1","usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}}}

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        var callUpdates = updates
            .SelectMany(u => u.Contents.OfType<FunctionCallContent>())
            .ToList();
        Assert.Equal(4, callUpdates.Count); // added + 2 deltas + done

        // Every update keeps the accumulated call_id/name (delta pieces carry neither).
        Assert.All(callUpdates, c =>
        {
            Assert.Equal("call_1", c.CallId);
            Assert.Equal("read_file", c.Name);
        });

        // added: no arguments yet.
        Assert.Empty(callUpdates[0].Arguments);

        // Delta 1: accumulated "{\"file" — cut ON A KEY (no value to close): RepairJson
        // cannot salvage it, the partial dictionary stays empty.
        Assert.Empty(callUpdates[1].Arguments);

        // Delta 2: accumulated "{\"filePath\":\"a.cs\"}" — fully parseable.
        var second = callUpdates[2].Arguments;
        Assert.Equal("a.cs", Assert.IsType<JsonElement>(second["filePath"]).GetString());

        // done: the complete arguments.
        var final = callUpdates[3].Arguments;
        Assert.Equal("a.cs", Assert.IsType<JsonElement>(final["filePath"]).GetString());
    }

    [Fact(Timeout = 20000)]
    public async Task Streaming_FunctionCallArguments_CutInsideValue_YieldsPartialDictionary()
    {
        // Fragmentation INSIDE a value (after the colon): RepairJson closes the string →
        // a NON-empty partial dictionary mid-stream (the progressive UI case).
        var sse = """
            event: response.output_item.added

            data: {"type":"response.output_item.added","item":{"type":"function_call","id":"item_1","call_id":"call_1","name":"read_file","arguments":""}}

            event: response.function_call_arguments.delta

            data: {"type":"response.function_call_arguments.delta","item_id":"item_1","delta":"{\"filePath\":\"a"}

            event: response.function_call_arguments.delta

            data: {"type":"response.function_call_arguments.delta","item_id":"item_1","delta":".cs\"}"}

            event: response.output_item.done

            data: {"type":"response.output_item.done","item":{"type":"function_call","id":"item_1","call_id":"call_1","name":"read_file","arguments":"{\"filePath\":\"a.cs\"}"}}

            event: response.completed

            data: {"type":"response.completed","response":{"id":"resp_1","usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}}}

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        var calls = updates.SelectMany(u => u.Contents.OfType<FunctionCallContent>()).ToList();
        Assert.Equal(4, calls.Count); // added + 2 deltas + done

        // added: empty.
        Assert.Empty(calls[0].Arguments);

        // Delta 1: accumulated "{\"filePath\":\"a" — cut inside the value: RepairJson closes
        // the string → partial dict with the truncated value.
        var partial = calls[1].Arguments;
        Assert.NotEmpty(partial);
        Assert.Equal("a", Assert.IsType<JsonElement>(partial["filePath"]).GetString());

        // Delta 2: accumulated "{\"filePath\":\"a.cs\"}" — fully parseable.
        var second = calls[2].Arguments;
        Assert.Equal("a.cs", Assert.IsType<JsonElement>(second["filePath"]).GetString());

        // done: the complete arguments.
        Assert.Equal("a.cs", Assert.IsType<JsonElement>(calls[3].Arguments["filePath"]).GetString());
    }

    // ------------------------------------------------------------------
    // (c2) ModelId: response.completed carries response.model (streaming never set it)
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Streaming_CompletedEvent_SetsModelId()
    {
        var sse = """
            event: response.output_text.delta

            data: {"type":"response.output_text.delta","delta":"answer"}

            event: response.completed

            data: {"type":"response.completed","response":{"id":"resp_1","model":"gpt-test","usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}}}

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        // ModelId = the model (not the response id resp_...).
        Assert.Contains(updates, u => u.ModelId == "gpt-test");
        Assert.DoesNotContain(updates, u => u.ModelId == "resp_1");
    }

    // ------------------------------------------------------------------
    // (c3) response.incomplete → FinishReason.Length (truncated answer)
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Streaming_IncompleteEvent_MapsToLengthFinishReason()
    {
        var sse = """
            event: response.output_text.delta

            data: {"type":"response.output_text.delta","delta":"partial answer"}

            event: response.incomplete

            data: {"type":"response.incomplete","response":{"id":"resp_1","model":"gpt-test","usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}}}

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        // The truncated response is marked Length, not silently ignored.
        var final = updates.Last();
        Assert.Equal("length", final.FinishReason?.Value);
        Assert.Equal("gpt-test", final.ModelId);
    }

    // ------------------------------------------------------------------
    // (c) usage from response.completed (incl. reasoning tokens)
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task Streaming_CompletedEvent_MapsUsageWithReasoningTokens()
    {
        var sse = """
            event: response.output_text.delta

            data: {"type":"response.output_text.delta","delta":"answer"}

            event: response.completed

            data: {"type":"response.completed","response":{"id":"resp_1","usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15,"output_tokens_details":{"reasoning_tokens":3}}}}

            """;
        using var server = StartSseServer(sse);
        using var client = CreateClient(server);

        var updates = await CollectUpdates(client);

        var usage = updates
            .SelectMany(u => u.Contents)
            .OfType<UsageContent>()
            .Single().Details;
        Assert.Equal(10, usage.InputTokenCount);
        Assert.Equal(5, usage.OutputTokenCount);
        Assert.Equal(15, usage.TotalTokenCount);
        Assert.Equal(3, usage.ReasoningTokenCount);
    }

    // ------------------------------------------------------------------
    // (d) non-streaming: output_text / reasoning summary / function_call
    // ------------------------------------------------------------------

    [Fact(Timeout = 20000)]
    public async Task NonStreaming_MapsOutputReasoningAndFunctionCall()
    {
        var json = """
            {
              "id": "resp_1",
              "model": "test-model",
              "status": "completed",
              "output": [
                {"type": "reasoning", "summary": [{"type": "summary_text", "text": "thinking"}]},
                {"type": "message", "content": [{"type": "output_text", "text": "answer"}]},
                {"type": "function_call", "call_id": "call_1", "name": "read_file", "arguments": "{\"filePath\":\"a.cs\"}"}
              ],
              "usage": {"input_tokens": 10, "output_tokens": 5, "total_tokens": 15}
            }
            """;
        var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/v1/responses").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(json));
        using var client = CreateClient(server, stream: false);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        var contents = response.Messages.Single().Contents;
        Assert.Contains(contents, c => c is TextContent t && t.Text == "answer");
        Assert.Contains(contents, c => c is TextReasoningContent r && r.Text == "thinking");

        var call = Assert.Single(contents.OfType<FunctionCallContent>());
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("a.cs", Assert.IsType<JsonElement>(call.Arguments["filePath"]).GetString());

        // ModelId = the model (NOT the response id resp_...).
        Assert.Equal("test-model", response.ModelId);
    }

    [Fact(Timeout = 20000)]
    public async Task NonStreaming_IncompleteStatus_MapsToLengthFinishReason()
    {
        var json = """
            {
              "id": "resp_1",
              "model": "gpt-test",
              "status": "incomplete",
              "output": [
                {"type": "message", "content": [{"type": "output_text", "text": "truncated"}]}
              ],
              "usage": {"input_tokens": 10, "output_tokens": 5, "total_tokens": 15}
            }
            """;
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/v1/responses").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(json));
        using var client = CreateClient(server, stream: false);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken);

        // Truncated response: Length (not Stop), ModelId = the model (not resp_1).
        Assert.Equal("length", response.FinishReason?.Value);
        Assert.Equal("gpt-test", response.ModelId);
    }
}
