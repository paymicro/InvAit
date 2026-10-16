using System.Net;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Regression tests for the OpenAI SDK transport quirks surfaced by the MS Agents Framework
/// migration (AfChatClientFactory + DynamicEnvironmentHttpMessageHandler):
/// 1. the SDK adds x-stainless-* telemetry headers to every request - they must be stripped
///    before the VS Code proxy headers dictionary is built and before base.SendAsync;
/// 2. the SDK's built-in retry (System.ClientModel ClientRetryPolicy, default maxRetries=3)
///    spams 4 identical requests per failure - it must be disabled (app-level RetryHandler
///    already covers transient failures).
/// </summary>
public class AfSdkTransportTests
{
    // ------------------------------------------------------------------
    // (a) x-stainless-* headers are stripped by DynamicEnvironmentHttpMessageHandler
    // ------------------------------------------------------------------

    /// <summary>
    /// JS stub: 'eval' (environment probe) throws - the handler treats the environment as
    /// non-VS Code; console.log / InvokeVoidAsync succeed (completed task).
    /// </summary>
    private sealed class NonVsCodeJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[] args)
            => identifier == "eval"
                ? throw new JSException("eval is not available in tests")
                : ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[] args)
            => InvokeAsync<TValue>(identifier, args);

        public ValueTask InvokeVoidAsync(string identifier, object?[] args)
            => ValueTask.CompletedTask;

        public ValueTask InvokeVoidAsync(string identifier, CancellationToken cancellationToken, object?[] args)
            => ValueTask.CompletedTask;
    }

    /// <summary>Inner handler that captures the request reaching the transport.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Captured;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }

    // ------------------------------------------------------------------
    // (b) SDK built-in retry is disabled (single request per failure)
    // ------------------------------------------------------------------

    /// <summary>Local HTTP server that answers 500 to every request and counts them.</summary>
    private sealed class AlwaysFailingServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _pump;
        public int Requests;

        public AlwaysFailingServer()
        {
            var port = new TcpPortProbe().Port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/v1/");
            _listener.Start();
            _pump = Task.Factory.StartNew(PumpAsync, TaskCreationOptions.LongRunning).Unwrap();
            BaseAddress = $"http://127.0.0.1:{port}";
        }

        public string BaseAddress { get; }

        private async Task PumpAsync()
        {
            try
            {
                while (true)
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleAsync(ctx));
                }
            }
            catch (Exception) when (_listener.IsListening == false)
            {
                // stopped
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            Interlocked.Increment(ref Requests);
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json";
            var body = System.Text.Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"boom\"}}");
            await ctx.Response.OutputStream.WriteAsync(body, TestContext.Current.CancellationToken);
            try { ctx.Response.Close(); } catch { }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _pump.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }

        private sealed class TcpPortProbe
        {
            public int Port { get; }

            public TcpPortProbe()
            {
                var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                l.Start();
                var ep = (IPEndPoint)l.LocalEndpoint;
                l.Stop();
                Port = ep.Port;
            }
        }
    }

    /// <summary>
    /// The real OpenAI SDK client built by AfChatClientFactory must send exactly ONE request
    /// when the server answers 500 (no built-in ClientRetryPolicy retries; app-level
    /// RetryHandler owns the retry policy).
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CreateChatClient_SdkRetryDisabled_SingleRequestPerFailure()
    {
        using var server = new AlwaysFailingServer();

        var profile = new ConnectionProfile
        {
            Endpoint = server.BaseAddress,
            ApiKey = "k",
            Model = "m",
            Stream = false,
            Temperature = 0.7f,
            MaxTokens = 1000
        };

        using var httpClient = new HttpClient();
        IChatClient client = AfChatClientFactory.Create(profile, httpClient);

        // Chat Completions protocol: the SDK throws ClientResultException on 500.
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, server.Requests);
    }

    /// <summary>
    /// Same regression for the Responses protocol branch (ResponsesClientOptions).
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CreateResponsesClient_SdkRetryDisabled_SingleRequestPerFailure()
    {
        using var server = new AlwaysFailingServer();

        var profile = new ConnectionProfile
        {
            Endpoint = server.BaseAddress,
            ApiKey = "k",
            Model = "m",
            Stream = false,
            Temperature = 0.7f,
            MaxTokens = 1000,
            ApiProviderType = ApiProvider.Responses
        };

        using var httpClient = new HttpClient();
        IChatClient client = AfChatClientFactory.Create(profile, httpClient);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, server.Requests);
    }
}
