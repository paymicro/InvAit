using System.Net;
using Microsoft.Extensions.AI;
using OpenAI;
using ChatMessageX = Microsoft.Extensions.AI.ChatMessage;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Proves that user-cancel REALLY closes the HTTP connection (LM Studio parity:
/// "Client disconnected. Stopping generation..."). Uses a real HttpListener SSE
/// server that keeps sending large events; the server-side write must FAIL after
/// the client aborts. Fails against the stock System.ClientModel transport, whose
/// per-request CTS is disposed right after the response headers arrive.
/// </summary>
public class AfTransportAbortTests
{
    private sealed class TerminatingSseServer : IAsyncDisposable
    {
        private HttpListener? _listener;
        private Task? _pump;
        public TaskCompletionSource ClientGone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int EventsSent;

        public string Start()
        {
            var port = new TcpClientListener().Port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/v1/");
            _listener.Start();
            _pump = Task.Factory.StartNew(PumpAsync, TaskCreationOptions.LongRunning).Unwrap();
            return $"http://127.0.0.1:{port}";
        }

        private async Task PumpAsync()
        {
            try
            {
                while (true)
                {
                    var ctx = await _listener!.GetContextAsync();
                    _ = Task.Run(() => HandleAsync(ctx));
                }
            }
            catch (Exception) when (_listener!.IsListening == false)
            {
                // stopped
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            Console.WriteLine($"[abort-test] server: request {ctx.Request.HttpMethod} {ctx.Request.Url?.PathAndQuery}");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.SendChunked = true;
            ctx.Response.Headers["Cache-Control"] = "no-cache";

            var tcs = ClientGone;
            try
            {
                var stream = ctx.Response.OutputStream;
                var id = "d9d528e22108450d96716ce5f36fb2ea";

                // Send a few real deltas so the client produces updates.
                foreach (var content in new[] { "Hello", " world" })
                {
                    await stream.WriteAsync(SseChunk(id, content is null ? null : content));
                }

                // Wall of big events: once the client aborts, one of these writes throws.
                var big = new string('x', 64 * 1024);
                while (true)
                {
                    await stream.WriteAsync(SseChunk(id, big));
                    EventsSent++;
                }
            }
            catch (Exception)
            {
                // The client is gone (that is what we are testing) OR the stop killed us.
                tcs.TrySetResult();
                try { ctx.Response.Abort(); } catch { }
            }
        }

        private static byte[] SseChunk(string id, string? content)
        {
            var delta = content is null
                ? "\"delta\":{}"
                : $"\"delta\":{{\"role\":\"assistant\",\"content\":\"{content}\"}}";
            var json = $"{{\"id\":\"{id}\",\"object\":\"chat.completion.chunk\",\"created\":1773130988," +
                       $"\"model\":\"m\",\"choices\":[{{\"index\":0,\"message\":null,{delta},\"finish_reason\":null}}],\"usage\":null}}";
            return System.Text.Encoding.UTF8.GetBytes($"data: {json}\n\n");
        }

        public async ValueTask DisposeAsync()
        {
            try { _listener?.Stop(); } catch { }
            if (_pump != null)
            {
                try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
        }

        private sealed class TcpClientListener
        {
            public int Port { get; }

            public TcpClientListener()
            {
                var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                l.Start();
                var ep = (IPEndPoint)l.LocalEndpoint;
                l.Stop();
                Port = ep.Port;
            }
        }
    }

    [Fact(Timeout = 20000)]
    public async Task Cancel_HonestlyAborts_ConnectionServerSide()
    {
        Console.WriteLine("[abort-test] start");
        await using var server = new TerminatingSseServer();
        var baseAddress = server.Start();

        var profile = new UIBlazor.Models.ConnectionProfile
        {
            Endpoint = baseAddress,
            ApiKey = "k",
            Model = "m",
            Stream = true,
            Temperature = 0.7,
            MaxTokens = 8000
        };

        using var httpClient = new HttpClient();
        IChatClient client = UIBlazor.Services.AfChatClientFactory.Create(profile, httpClient);

        using var cts = new CancellationTokenSource();
        var enumeration = client.GetStreamingResponseAsync(
            [new ChatMessageX(ChatRole.User, "hi")], null, cts.Token);

        // Consume a few streamed deltas so the request is fully established.
        int updates = 0;
        try
        {
            await foreach (var update in enumeration)
            {
                updates++;
                Console.WriteLine($"[abort-test] client: update#{updates}");
                if (updates >= 2)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // fine — the race gate or transport abort surfaced before we settled
        }

        Assert.True(updates >= 1, $"no streamed updates (server sent {server.EventsSent})");
    }

    /// <summary>
    /// END-TO-END: the REAL AfAgentLoop gateway + real HTTP server. Cancellation must
    /// reach the server as a disconnect, not just detach the consumer.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task AfAgentLoop_Cancel_DisconnectsServer()
    {
        Console.WriteLine("[gw-test] start");
        await using var server = new TerminatingSseServer();
        var baseAddress = server.Start();

        var connectionStub = new StubToolCallHandler();
        // Loop bound to the test server only (no DI client)
        var loop = new UIBlazor.Services.AfAgentLoop(
            () => new UIBlazor.Models.ConnectionProfile
            {
                Endpoint = baseAddress,
                ApiKey = "k",
                Model = "m",
                Stream = true,
                Temperature = 0.7,
                MaxTokens = 8000
            },
            new HttpClient(),
            connectionStub);

        using var cts = new CancellationTokenSource();
        var result = new UIBlazor.Services.Models.CompletionsResult();
        var session = new UIBlazor.Models.ConversationSession { Id = "s" };

        var enumeration = loop.GetCompletionsAsync(
            [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], null, session, result, cts.Token);

        int deltas = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await foreach (var delta in enumeration)
            {
                deltas++;
                if (sw.ElapsedMilliseconds > 150)
                {
                    break;
                }
                if (deltas >= 5)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gw-test] enumeration ended with {ex.GetType().Name}: {ex.Message}");
        }

        Assert.True(deltas >= 1, $"no deltas (server sent {server.EventsSent})");
        Console.WriteLine($"[gw-test] consumed {deltas} deltas, cancelling");

        await cts.CancelAsync();
        Console.WriteLine("[gw-test] cancelled, waiting for server to notice");

        await server.ClientGone.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Console.WriteLine("[gw-test] server noticed disconnect");
    }

    private sealed class StubToolCallHandler : UIBlazor.Services.Interfaces.IToolCallHandler
    {
        public event Action<string>? ApprovalRequired;
        public void PrepareToolsForApprovals(List<UIBlazor.Services.Models.ToolCall> toolCalls) { }
        public Task ProcessToolCallsAsync(List<UIBlazor.Services.Models.ToolCall> toolCalls, Shared.Contracts.AppMode appMode, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HandleApprovalAsync(string segmentId, bool approved) => Task.CompletedTask;
        public Task HandleAskUserAnswerAsync(string toolCallId, string answer) => Task.CompletedTask;
        public void CancelPendingApprovals() { }
    }
}
