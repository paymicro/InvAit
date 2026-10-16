using System.ClientModel.Primitives;
using System.Net;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using UIBlazor.Services.AgentFramework;


// Radzen.Blazor (global usings in test infra) also declares ChatMessage.
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessageRole = UIBlazor.Constants.ChatMessageRole;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Cancellation probes for the AF streaming path: a slow trickle SSE source, cancel
/// mid-stream, measure observation latency. A transport that keeps delivering after the
/// request token fired (proxy fetch / buffered channel) is covered explicitly.
/// </summary>
public class AfCancelProbeTests
{
    private const string SseBody =
        "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":null,\"delta\":{\"role\":\"assistant\",\"content\":\"chunk of content \"},\"finish_reason\":null}],\"usage\":null}\n\n";

    /// <summary>
    /// SSE handler that trickles a chunk every <paramref name="delayMs"/> for
    /// <paramref name="chunkCount"/> chunks. When <paramref name="ignoresCancel"/> is true
    /// the stream keeps delivering even after the request token fired (broken transport).
    /// </summary>
    private sealed class TrickleHandler : HttpMessageHandler
    {
        private readonly bool _ignoresCancel;
        private readonly int _delayMs;
        private readonly int _chunkCount;
        private int _chunksSent;

        public TrickleHandler(bool ignoresCancel, int delayMs, int chunkCount)
        {
            _ignoresCancel = ignoresCancel;
            _delayMs = delayMs;
            _chunkCount = chunkCount;
        }

        public int ChunksSent => _chunksSent;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new TrickleStream(this))
            };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            return Task.FromResult(response);
        }

        private sealed class TrickleStream(TrickleHandler owner) : Stream
        {
            private int _sent;
            private byte[]? _remaining;
            private int _remainingOffset;

            private int NextChunk(Memory<byte> buffer)
            {
                if (_remaining != null)
                {
                    var toCopy = Math.Min(buffer.Length, _remaining.Length - _remainingOffset);
                    _remaining.AsSpan(_remainingOffset, toCopy).CopyTo(buffer.Span);
                    _remainingOffset += toCopy;
                    if (_remainingOffset >= _remaining.Length) _remaining = null;
                    return toCopy;
                }

                var payload = Encoding.UTF8.GetBytes(SseBody);
                var n = Math.Min(payload.Length, buffer.Length);
                payload.AsSpan(0, n).CopyTo(buffer.Span);
                if (n < payload.Length)
                {
                    _remaining = payload;
                    _remainingOffset = n;
                }
                return n;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_sent >= owner._chunkCount && _remaining == null)
                {
                    return 0;
                }

                // ignoresCancel: the delay never observes the request token (broken transport).
                var observedToken = owner._ignoresCancel ? CancellationToken.None : cancellationToken;
                try
                {
                    await Task.Delay(owner._delayMs, observedToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                _sent++;
                owner._chunksSent = _sent;
                return NextChunk(buffer);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => 1;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => NextChunk(buffer.AsMemory(offset, count));
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Regression for the frozen-UI-on-cancel bug: the race observes the token promptly, but
    /// the abandoned enumerator's DisposeAsync cannot unwind while the stuck MoveNext is
    /// still in flight. The cancel path must NOT wait for that disposal (it abandoned the
    /// enumerator fire-and-forget) — OCE must surface immediately.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Cancel_StuckDispose_DoesNotBlockUnwind()
    {
        var stuck = new StuckStream();
        using var cts = new CancellationTokenSource();

        var enumeration = ConsumeGuardedAsync(stuck, cts.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumeration);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"cancelled observable after {sw.ElapsedMilliseconds}ms");
    }

    private static async Task<string> ConsumeGuardedAsync(
        IAsyncEnumerable<int> source, CancellationToken token)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var delta in AfCancelRace.GuardAsync(
            source, () => "Request timed out", token))
        {
            text.Append(delta);
        }
        return text.ToString();
    }

    /// <summary>
    /// Enumerator that keeps the in-flight MoveNext pending forever (stuck transport) and
    /// sleeps 10 seconds in DisposeAsync (what an async-iterator dispose effectively does
    /// while waiting for the abandoned MoveNext to unwind).
    /// </summary>
    private sealed class StuckStream : IAsyncEnumerable<int>
    {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new Enumerator();

        private sealed class Enumerator : IAsyncEnumerator<int>
        {
            private readonly TaskCompletionSource<bool> _stuck =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int Current => 0;

            public ValueTask<bool> MoveNextAsync() => new(_stuck.Task);

            public async ValueTask DisposeAsync()
            {
                await Task.Delay(10_000, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static AfAgentLoop CreateLoop(TrickleHandler handler)
    {
        var client = new HttpClient(handler);
        return new AfAgentLoop(
            () => new UIBlazor.Models.ConnectionProfile { Endpoint = "http://proxy.local", ApiKey = "key", Model = "m", Stream = true, Temperature = 0.7, MaxTokens = 1000 },
            client,
            new StubToolCallHandler());
    }

    private static async Task<string> ConsumeAll(AfAgentLoop loop, CancellationToken token)
    {
        var session = new UIBlazor.Models.ConversationSession { Id = "s" };
        var capture = new UIBlazor.Services.Models.CompletionsResult();
        var text = new System.Text.StringBuilder();

        await foreach (var delta in loop.GetCompletionsAsync(
            [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], null, session, capture, token))
        {
            text.Append(delta.Content);
        }
        return text.ToString();
    }

    [Fact]
    public async Task Cancel_StuckRead_RaceObservesPromptly()
    {
        // Regression for the frozen-cancel bug: a transport whose low-level read never
        // completes (WASM fetch / VS Code proxy cannot abort a streaming body). The AF
        // loop must race the MoveNext against the token and surface cancellation
        // immediately, NOT wait for the read (10s) to finish downstream.
        var handler = new TrickleHandler(ignoresCancel: true, delayMs: 10_000, chunkCount: 500);
        var loop = CreateLoop(handler);
        using var cts = new CancellationTokenSource();

        var enumeration = ConsumeAll(loop, cts.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumeration);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"observable after {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Cancel_HonestTransport_StopsPromptly()
    {
        // Honest transport (reads observe the request token): cancel mid-stream →
        // OperationCanceled surfaces and the source stops producing quickly.
        var handler = new TrickleHandler(ignoresCancel: false, delayMs: 100, chunkCount: 500);
        var loop = CreateLoop(handler);
        using var cts = new CancellationTokenSource();

        var enumeration = ConsumeAll(loop, cts.Token);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumeration);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"observable after {sw.ElapsedMilliseconds}ms; handler kept sending {handler.ChunksSent} chunks");
    }

    /// <summary>
    /// Full-stack regression for the 33s freeze on cancel: a tool awaits approval, the user
    /// cancels, FICC proceeds to its next model request whose transport is stuck forever
    /// (WASM fetch cannot abort a streaming body). The guard race must surface OCE promptly
    /// instead of hanging on the stuck MoveNext.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Cancel_PendingApproval_StuckNextRequest_RaceSurfaces()
    {
        var approvalShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var toolManagerMock = new Mock<IToolManager>();
        toolManagerMock.Setup(t => t.GetTool("delegate_task")).Returns(new Tool
        {
            Name = "delegate_task",
            DisplayName = "Delegate",
            Enabled = true,
            NativeTool = new NativeToolDefinition
            {
                Function = new NativeToolFunction
                {
                    Name = "delegate_task",
                    Description = "test",
                    Strict = true,
                    Parameters = new NativeParameters
                    {
                        Type = NativeToolType.Object,
                        Properties = new() { ["task"] = new NativePropertyDefinition { Type = NativeToolType.String, Description = "task" } },
                        Required = ["task"]
                    }
                }
            },
            ExecuteAsync = (_, _) => throw new InvalidOperationException("must not execute without approval")
        });
        toolManagerMock.Setup(t => t.GetApprovalModeByToolName("delegate_task")).Returns(ToolApprovalMode.Ask);

        var realHandler = new ToolCallHandler(toolManagerMock.Object);
        realHandler.ApprovalRequired += _ => approvalShown.TrySetResult();

        var client = new HangOnSecondCallChatClient();
        var loop = new AfAgentLoop(
            () => new UIBlazor.Models.ConnectionProfile { Endpoint = "https://api.test", ApiKey = "key", Model = "m", Stream = true, Temperature = 0.7, MaxTokens = 1000 },
            new HttpClient(),
            realHandler,
            (_, _) => client);
        var session = new ConversationSession { Id = "s" };
        var capture = new UIBlazor.Services.Models.CompletionsResult();
        using var cts = new CancellationTokenSource();

        var tools = new[]
        {
            new Tool
            {
                Name = "delegate_task",
                NativeTool = new NativeToolDefinition
                {
                    Function = new NativeToolFunction
                    {
                        Name = "delegate_task",
                        Description = "test",
                        Strict = true,
                        Parameters = new NativeParameters
                        {
                            Type = NativeToolType.Object,
                            Properties = new() { ["task"] = new NativePropertyDefinition { Type = NativeToolType.String, Description = "task" } },
                            Required = ["task"]
                        }
                    }
                },
                ExecuteAsync = (_, _) => throw new InvalidOperationException("must not execute without approval")
            }
        };

        var enumerateTask = Task.Run(async () =>
        {
            await foreach (var _ in loop.GetCompletionsAsync(
                [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], tools, session, capture, cts.Token).ConfigureAwait(false))
            {
            }
        });

        // Wait until the tool is waiting for approval, cancel while it is parked there.
        await approvalShown.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerateTask);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"cancel observable after {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// IChatClient: first call streams a delegate_task FunctionCallContent, second call returns
    /// a stream whose MoveNextAsync never completes and whose DisposeAsync hangs - the shape of
    /// a WASM fetch that cannot be aborted once issued.
    /// </summary>
    private sealed class HangOnSecondCallChatClient : IChatClient
    {
        private int _call;

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _call);
            return call == 1
                ? FirstTurn()
                : HangForever();
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> FirstTurn()
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m1",
                Contents = [new FunctionCallContent("c1", "delegate_task", new Dictionary<string, object?> { ["task"] = "do it" })]
            };
            yield return new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                MessageId = "m1",
                FinishReason = Microsoft.Extensions.AI.ChatFinishReason.ToolCalls
            };
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> HangForever()
        {
            var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await never.Task.ConfigureAwait(false);
            yield break;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("unexpected non-streaming call");

        public object? GetService(Type serviceType, object? serviceKey) => null;
        public void Dispose() { }
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
