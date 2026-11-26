namespace UIBlazor.Tests.VS;

using System.Diagnostics;

/// <summary>
/// Tests for the host-response path of <see cref="VsBridge"/>.
/// <para>
/// Contract under test (<see cref="VsResponse"/> is bimodal, discriminated by Success —
/// same shape as MCP <c>CallToolResult { content, isError }</c>):
/// </para>
/// <list type="bullet">
/// <item>a host answer with <c>Success = false</c> (file not found, no read permission, …)
/// is a RESULT, not a transport failure: no exception, no rewritten text;</item>
/// <item>it must be logged as WARN — never through the error channel, because Blazor WASM
/// shows the #blazor-error-ui banner whenever something is written to Console.Error;</item>
/// <item>an empty failure payload must still surface a non-empty <c>ErrorMessage</c>, otherwise
/// <c>ToolResult.Convert</c> would hand the LLM an empty tool result.</item>
/// </list>
/// <para>
/// The fake JS runtime replaces both the host (answering postVsMessage synchronously) and the
/// log sink (collecting log_error payloads), so these tests need no Moq generic gymnastics and
/// never touch Console.SetOut/SetError — the test assembly runs fully parallel (MaxThreads 16)
/// and a global console redirect would be a race.
/// </para>
/// </summary>
public class VsBridgeTests
{
    private const string FileMissingPayload = "File \"missing.cs\" doesn't exist.";

    private static VsBridge CreateBridge(IJSRuntime jsRuntime)
    {
        var settings = new Mock<ICommonSettingsProvider>();
        settings.SetupGet(s => s.Current).Returns(new CommonOptions { ToolTimeoutMs = 5_000 });

        var contextService = new Mock<IContextService>();

        return new VsBridge(jsRuntime, settings.Object, contextService.Object);
    }

    /// <summary>Makes the fake host answer every request with Success = false ("file read failed").</summary>
    private static void FailHostRequests(FakeVsHostJs js, VsBridge bridge, string? payload = FileMissingPayload)
        => js.Responder = request => bridge.HandleVsResponseAsync(new VsResponse
        {
            CorrelationId = request.CorrelationId,
            Success = false,
            Payload = payload,
        });

    // -----------------------------------------------------------------------
    // Timeout ownership: the client must wait LONGER than the host kills
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(20_000)]
    [InlineData(120_000)]
    [InlineData(1_800_000)]
    public void ComputeWait_IsStrictlyGreaterThanHostLimit(int settingMs)
    {
        // The host kills the command at `settingMs` and answers "Command timed out after N ms".
        // A client that waits exactly the same amount wins that race often enough to replace the
        // honest answer with its own transport failure — and the output of the killed command is
        // then read by nobody.
        var wait = VsBridge.ComputeWait(BuiltInToolEnum.Bash, settingMs);

        Assert.True(wait > TimeSpan.FromMilliseconds(settingMs));
        Assert.Equal(TimeSpan.FromMilliseconds(settingMs + ToolTimeout.ClientGraceMs), wait);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ComputeWait_NonPositiveSetting_FallsBackToToolDefault(int settingMs)
    {
        // Same channel rule as the host: a non-positive value means "the tool default",
        // never "no limit" and never "give up immediately".
        Assert.Equal(
            TimeSpan.FromMilliseconds(ToolTimeout.DefaultCommandMs + ToolTimeout.ClientGraceMs),
            VsBridge.ComputeWait(BuiltInToolEnum.Bash, settingMs));
    }

    [Fact]
    public void ComputeWait_McpCall_AlsoOutlastsTheHostChain()
    {
        // The MCP chain is longer than the call itself: ToolExecutor waits `timeoutMs + McpHostGraceMs`
        // for the McpHost process. A client that only waited the bash amount would simply move the
        // lost race into the MCP channel.
        var settingMs = 60_000;

        var wait = VsBridge.ComputeWait(BasicEnum.McpCallTool, settingMs);

        Assert.True(wait > TimeSpan.FromMilliseconds(settingMs + ToolTimeout.McpHostGraceMs));
        Assert.Equal(
            TimeSpan.FromMilliseconds(settingMs + ToolTimeout.McpHostGraceMs + ToolTimeout.ClientGraceMs),
            wait);
    }

    [Fact]
    public void ComputeWait_MaxIntSetting_SaturatesInsteadOfGoingNegative()
    {
        // The setting is loaded from localStorage, not from the slider: a corrupted value near
        // int.MaxValue overflowed `int` addition and produced a NEGATIVE TimeSpan, which
        // `CancellationTokenSource` rejects with ArgumentOutOfRangeException — every tool call
        // would throw instead of "waits as long as possible".
        var wait = VsBridge.ComputeWait(BuiltInToolEnum.Bash, int.MaxValue);

        Assert.True(wait > TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMilliseconds(int.MaxValue), wait);
    }

    [Fact]
    public async Task ExecuteToolAsync_HostReturnsError_ReturnsFailureWithOriginalText()
    {
        // Arrange
        var js = new FakeVsHostJs();
        var bridge = CreateBridge(js);
        FailHostRequests(js, bridge);

        // Act
        var result = await bridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, "{}", TestContext.Current.CancellationToken);

        // Assert — the original host text survives verbatim (no exception was thrown and wrapped)
        Assert.False(result.Success);
        Assert.Equal(FileMissingPayload, result.ErrorMessage);
        Assert.Equal(FileMissingPayload, result.Result);
        Assert.Equal(BuiltInToolEnum.ReadFiles, result.Name);
    }

    [Fact]
    public async Task ExecuteToolAsync_HostReturnsErrorWithoutPayload_SynthesizesErrorMessage()
    {
        // Arrange — the failure text is missing: the LLM must still get something meaningful
        var js = new FakeVsHostJs();
        var bridge = CreateBridge(js);
        FailHostRequests(js, bridge, payload: null);

        // Act
        var result = await bridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, "{}", TestContext.Current.CancellationToken);

        // Assert — never empty: ToolResult.Convert feeds ErrorMessage to the model on failure
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Equal("Request failed", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteToolAsync_HostReturnsError_LogsWarningToHost()
    {
        // Arrange
        var js = new FakeVsHostJs();
        var bridge = CreateBridge(js);
        FailHostRequests(js, bridge);

        // Act
        await bridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, "{}", TestContext.Current.CancellationToken);

        // Assert — the failure is still logged (WARN), so it stays visible in devtools/Output
        await WaitUntilAsync(() => js.Logs.Count > 0);

        var entry = Assert.Single(js.Logs);
        Assert.Equal(LogEntry.WarnLevel, entry.Level);
        Assert.Equal("VsBridge", entry.Source);
        Assert.Contains(BuiltInToolEnum.ReadFiles, entry.Message, StringComparison.Ordinal);
        Assert.Contains("doesn't exist", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(js.Logs, l => l.Level == LogEntry.ErrorLevel);
    }

    [Fact]
    public async Task ExecuteToolAsync_HostReturnsSuccess_DoesNotPostWarning()
    {
        // Arrange
        var js = new FakeVsHostJs();
        var bridge = CreateBridge(js);
        js.Responder = request => bridge.HandleVsResponseAsync(new VsResponse
        {
            CorrelationId = request.CorrelationId,
            Success = true,
            Payload = "ok",
        });

        // Act
        var result = await bridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, "{}", TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("ok", result.Result);
        Assert.Empty(result.ErrorMessage);
        Assert.Empty(js.Logs);
    }

    [Fact]
    public async Task ExecuteToolAsync_AfterHostError_BridgeStillServesNextRequest()
    {
        // Arrange — first answer is a failure, the second one succeeds
        var js = new FakeVsHostJs();
        var bridge = CreateBridge(js);
        var attempt = 0;
        js.Responder = request =>
        {
            var failed = attempt++ == 0;
            return bridge.HandleVsResponseAsync(new VsResponse
            {
                CorrelationId = request.CorrelationId,
                Success = !failed,
                Payload = failed ? "first failed" : "ok",
            });
        };

        // Act
        var first = await bridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, "{}", TestContext.Current.CancellationToken);
        var second = await bridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, "{}", TestContext.Current.CancellationToken);

        // Assert — a failed request leaves nothing pending and does not poison the next one
        Assert.False(first.Success);
        Assert.Equal("first failed", first.ErrorMessage);
        Assert.True(second.Success);
        Assert.Equal("ok", second.Result);
    }

    [Fact]
    public async Task ExecuteToolAsync_CancelledRun_PropagatesCancellation_NotAToolFailure()
    {
        // Arrange — the host never answers: the request is parked on the TCS, so the only way out
        // is the caller's token.
        var js = new FakeVsHostJs();
        var bridge = CreateBridge(js);
        js.Responder = null;

        using var cts = new CancellationTokenSource();

        // Act — cancel while the request is in flight.
        var pending = bridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, "{}", cts.Token);
        await WaitUntilAsync(() => js.Posted.Count == 1);
        await cts.CancelAsync();

        // Assert — cancellation surfaces as cancellation. Translating it into a failed tool result
        // handed the model "Transport failure: The operation was canceled." (a lie: the tool did not
        // fail, it was never finished), logged an ERROR with a stack trace for an expected event, and
        // forced the run loop to keep unwinding its batch one call at a time.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await WaitUntilAsync(() => js.Logs.Count > 0 || js.Posted.Count == 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(js.Logs, l => l.Level == LogEntry.ErrorLevel);
    }

    [Fact]
    public async Task HandleVsResponseAsync_ErrorResponseForUnknownRequest_DoesNotThrow()
    {
        // Arrange
        var js = new FakeVsHostJs();
        var bridge = CreateBridge(js);

        // Act
        var exception = await Record.ExceptionAsync(() => bridge.HandleVsResponseAsync(new VsResponse
        {
            CorrelationId = "no-such-request",
            Success = false,
            Payload = FileMissingPayload,
        }));

        // Assert — a late/duplicate answer must not throw: the request it belongs to is already gone
        Assert.Null(exception);
    }

    [Fact]
    public async Task HandleVsResponseAsync_UnknownCorrelationId_LeavesEvidenceInTheHostLog()
    {
        // A refusal the model did not cause ("The JSON value could not be converted to
        // Nullable<bool>...") is unexplainable without knowing WHICH request it answered. A crossed
        // or duplicated correlation id is the one mechanism that produces exactly that, and its only
        // symptom is an answer nobody awaits — which used to be a browser-console line, invisible
        // from the chat window and absent from the host log. The id is the evidence.
        var js = new FakeVsHostJs();
        var bridge = CreateBridge(js);

        await bridge.HandleVsResponseAsync(new VsResponse
        {
            CorrelationId = "the-other-call",
            Success = false,
            Payload = FileMissingPayload,
        });

        await WaitUntilAsync(() => js.Logs.Count > 0);
        var entry = Assert.Single(js.Logs);

        // WARN, never ERROR: a late answer for a timed-out request lands here too and is expected.
        Assert.Equal(LogEntry.WarnLevel, entry.Level);
        Assert.Contains("the-other-call", entry.Message);
        Assert.Contains("still pending", entry.Message);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail("Condition was not met within the timeout");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Minimal <see cref="IJSRuntime"/> stand-in: answers setVsBridgeHandler with "OK",
    /// records postVsMessage traffic, collects host log entries and lets the test answer
    /// each request synchronously (as the real WebView2 host would do via a callback).
    /// </summary>
    private sealed class FakeVsHostJs : IJSRuntime
    {
        public FakeVsHostJs(Func<VsRequest, Task>? responder = null) => Responder = responder;

        /// <summary>Requests sent to the host, excluding fire-and-forget log entries.</summary>
        public List<VsRequest> Posted { get; } = [];

        /// <summary>LogEntry payloads received via <see cref="BasicEnum.LogError"/>.</summary>
        public List<LogEntry> Logs { get; } = [];

        public Func<VsRequest, Task>? Responder { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "postVsMessage" || args is not { Length: > 0 } || args[0] is not VsRequest request)
                return Ok<TValue>();

            if (request.Action == BasicEnum.LogError)
            {
                if (request.Payload is not null && JsonUtils.Deserialize<LogEntry>(request.Payload) is { } entry)
                    Logs.Add(entry);

                return Ok<TValue>();
            }

            // Bootstrap ping sent from InitializeAsync: fire-and-forget, никто его не ждёт,
            // поэтому отвечать на него нельзя — иначе тест-хост «съедал» бы ответ на него.
            if (request.Action == BasicEnum.UIReady)
                return Ok<TValue>();

            Posted.Add(request);

            // HandleVsResponseAsync completes synchronously; blocking here keeps the fake
            // simple (the bridge always registers its pending request before invoking JS).
            Responder?.Invoke(request).GetAwaiter().GetResult();

            return Ok<TValue>();
        }

        private static ValueTask<TValue> Ok<TValue>()
            => typeof(TValue) == typeof(string)
                ? ValueTask.FromResult((TValue)(object)"OK")
                : ValueTask.FromResult<TValue>(default!);
    }
}
