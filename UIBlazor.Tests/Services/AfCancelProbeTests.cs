using System.Net;
using Microsoft.Extensions.AI;


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
    /// Regression for the UnobservedTaskException hole: when the abandoned MoveNext loses
    /// the cancel race, its task must carry a swallow-continuation — a late transport
    /// failure (faulted MoveNext AFTER the OCE surfaced) must not surface as an
    /// UnobservedTaskException. The OCE itself must still surface promptly.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Cancel_AbandonedMoveNextFaultsLater_NoUnobservedException()
    {
        var unobservedLateFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var stuck = new FaultingAfterCancelStream();
            using var cts = new CancellationTokenSource();

            var enumeration = ConsumeGuardedAsync(stuck, cts.Token);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            await cts.CancelAsync();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumeration);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"cancelled observable after {sw.ElapsedMilliseconds}ms");

            // Let the abandoned MoveNext fault (it fails 100ms after cancellation).
            await Task.Delay(500, TestContext.Current.CancellationToken);

            // Force finalization so an unobserved exception would surface deterministically
            // (the UTE handler fires synchronously inside the finalizer thread).
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // The swallow-continuation observes the faulted task — the late transport
            // failure must never reach UnobservedTaskException.
            Assert.False(unobservedLateFailure.Task.IsCompleted, "abandoned MoveNext surfaced as UnobservedTaskException");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            // Filter by the unique message: unrelated unobserved tasks from other tests
            // must not fail this test.
            if (e.Exception.InnerExceptions.Any(x => x.Message.Contains("late transport failure")))
            {
                unobservedLateFailure.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Enumerator whose MoveNextAsync stays pending until cancelled, then faults —
    /// the shape of a transport that fails only after the user gave up on it.
    /// </summary>
    private sealed class FaultingAfterCancelStream : IAsyncEnumerable<int>
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
                // The abandoned MoveNext faults shortly after the cancel (fire-and-forget teardown).
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                _stuck.TrySetException(new InvalidOperationException("late transport failure"));
            }
        }
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

    /// <summary>
    /// Single-shot adapter over the given transport — the SAME gateway every run uses today
    /// (the turn loop lives in the caller, so the adapter owns the cancel race and the timeout).
    /// </summary>
    private static AfCompletionsAdapter CreateAdapter(TrickleHandler handler)
    {
        var client = new HttpClient(handler);
        return new AfCompletionsAdapter(
            () => new UIBlazor.Models.ConnectionProfile { Endpoint = "http://proxy.local", ApiKey = "key", Model = "m", Stream = true, Temperature = 0.7f, MaxTokens = 1000 },
            client,
            () => 100);
    }

    private static async Task<string> ConsumeAll(AfCompletionsAdapter adapter, CancellationToken token)
    {
        var session = new UIBlazor.Models.ConversationSession { Id = "s" };
        var capture = new UIBlazor.Services.Models.CompletionsResult();
        var text = new System.Text.StringBuilder();

        await foreach (var delta in adapter.GetCompletionsAsync(
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
        var loop = CreateAdapter(handler);
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
        var loop = CreateAdapter(handler);
        using var cts = new CancellationTokenSource();

        var enumeration = ConsumeAll(loop, cts.Token);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumeration);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"observable after {sw.ElapsedMilliseconds}ms; handler kept sending {handler.ChunksSent} chunks");
    }

    /// <summary>
    /// Full-stack regression for the 33s freeze on cancel, split by OWNER because the turn loop
    /// no longer lives inside a gateway:
    /// <list type="number">
    /// <item>a tool parked on approval must release the cancel promptly (the handler must not
    /// hang on the gate) — otherwise the run cannot even reach its next turn;</item>
    /// <item>the NEXT request, whose transport is stuck forever (a WASM fetch cannot abort a
    /// streaming body), must surface cancellation through the adapter's guard race instead of
    /// hanging on the stuck <c>MoveNext</c>.</item>
    /// </list>
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Cancel_PendingApproval_StuckNextRequest_RaceSurfaces()
    {
        // ── Part 1: cancel while a tool waits for approval ──────────────────────────────
        var approvalShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = false;

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
            ExecuteAsync = (_, _) =>
            {
                executed = true;
                throw new InvalidOperationException("must not execute without approval");
            }
        });
        toolManagerMock.Setup(t => t.GetApprovalModeByToolName("delegate_task")).Returns(ToolApprovalMode.Ask);

        var handler = new ToolCallHandler(toolManagerMock.Object);
        handler.ApprovalRequired += _ => approvalShown.TrySetResult();

        var parkedCall = new ToolCall
        {
            Id = "c1",
            Function = new ToolCallFunction { Name = "delegate_task", Arguments = "{}" }
        };
        handler.PrepareToolsForApprovals([parkedCall]);

        using var approvalCts = new CancellationTokenSource();
        var batch = Task.Run(() => handler.ProcessToolCallsAsync([parkedCall], AppMode.Agent, approvalCts.Token),
            TestContext.Current.CancellationToken);

        await approvalShown.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var cancelSw = System.Diagnostics.Stopwatch.StartNew();
        await approvalCts.CancelAsync();
        await batch.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.True(cancelSw.ElapsedMilliseconds < 3000,
            $"approval wait released after {cancelSw.ElapsedMilliseconds}ms");

        // Nothing was executed (the tool body throws when invoked without an approval) and the
        // batch returned instead of hanging. The caller's EnsureToolResults fills in the Result
        // of a call the interrupted batch left unanswered.
        Assert.False(executed, "the tool must not run without an approval");

        // ── Part 2: the stuck NEXT request surfaces cancellation via the guard race ─────
        var session = new ConversationSession { Id = "s" };
        var capture = new UIBlazor.Services.Models.CompletionsResult();
        using var requestCts = new CancellationTokenSource();

        var adapter = new AfCompletionsAdapter(
            () => new UIBlazor.Models.ConnectionProfile
            {
                Endpoint = "https://api.test",
                ApiKey = "key",
                Model = "m",
                Stream = true,
                Temperature = 0.7f,
                MaxTokens = 1000
            },
            new HttpClient(),
            () => 100,
            (_, _) => new HangForeverChatClient());

        var enumeration = Task.Run(async () =>
        {
            await foreach (var _ in adapter.GetCompletionsAsync(
                [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], null, session, capture, requestCts.Token)
                .ConfigureAwait(false))
            {
            }
        }, TestContext.Current.CancellationToken);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        await requestCts.CancelAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumeration);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"cancel observable after {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// IChatClient whose stream never produces an update — the shape of a WASM fetch that cannot
    /// be aborted once issued.
    /// </summary>
    private sealed class HangForeverChatClient : IChatClient
    {
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => HangForever();

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

}
