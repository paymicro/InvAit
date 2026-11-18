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

    [Fact]
    public async Task ExecuteToolAsync_HostReturnsError_ReturnsFailureWithOriginalText()
    {
        // Arrange
        var js = new FakeVsHostJs();
        var bridge = CreateBridge(js);
        FailHostRequests(js, bridge);

        // Act
        var result = await bridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, "{}");

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
        var result = await bridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, "{}");

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
        await bridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, "{}");

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
        var result = await bridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, "{}");
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
        var first = await bridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, "{}");
        var second = await bridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, "{}");

        // Assert — a failed request leaves nothing pending and does not poison the next one
        Assert.False(first.Success);
        Assert.Equal("first failed", first.ErrorMessage);
        Assert.True(second.Success);
        Assert.Equal("ok", second.Result);
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

        // Assert — late/duplicate answers are dropped silently (console line only)
        Assert.Null(exception);
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
