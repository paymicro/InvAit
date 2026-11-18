namespace UIBlazor.Tests.Utils;

using System.Diagnostics;
using System.Linq.Expressions;

/// <summary>
/// Tests for <see cref="UiErrorLogger"/>: browser console output + fire-and-forget
/// host posting (log_error VsRequest via postVsMessage) with swallow-on-failure.
/// Note: InvokeVoidAsync is an extension method — the mock targets the underlying
/// IJSRuntime.InvokeAsync&lt;object&gt; instance method.
/// </summary>
public class UiErrorLoggerTests
{
    private static UiErrorLogger CreateLogger(Mock<IJSRuntime> jsMock)
        => new(jsMock.Object);

    [Fact]
    public async Task Log_WritesToBrowserConsole()
    {
        // Arrange - capture stdout (UiErrorLogger writes via Console.WriteLine with a level tag)
        var original = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var logger = CreateLogger(new Mock<IJSRuntime>());

            // Act
            logger.LogError("HTTP transport", "Stream failed");

            // Assert - browser console (devtools) gets the entry (fire-and-forget: poll)
            await WaitUntilAsync(() =>
            {
                var output = sw.ToString();
                return output.Contains(LogEntry.ErrorLevel)
                    && output.Contains("HTTP transport")
                    && output.Contains("Stream failed");
            });
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
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
    /// Fire-and-forget публикации может не завершиться к моменту синхронного возврата Log —
    /// верифицируем поллингом, а не сразу (иначе флаки при смене окружения).
    /// </summary>
    private static void WaitUntilVerified(Mock<IJSRuntime> jsMock, Expression<Func<IJSRuntime, ValueTask<object>>> verifyExpr)
    {
        WaitUntilAsync(() =>
        {
            try
            {
                jsMock.Verify(verifyExpr, Times.Once);
                return true;
            }
            catch (MockException)
            {
                return false;
            }
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public void Log_PostsLogErrorRequestToHost()
    {
        // Arrange
        var jsMock = new Mock<IJSRuntime>();
        jsMock
            .Setup(js => js.InvokeAsync<object>("postVsMessage", It.IsAny<object[]>()))
            .ReturnsAsync(new object());
        var logger = CreateLogger(jsMock);

        // Act
        logger.LogError("VsBridge", "Request failed", new InvalidOperationException("pipe broken"));

        // Assert - fire-and-forget postVsMessage with log_error action and serialized LogEntry payload
        // (fire-and-forget: verify via polling, not immediately after the sync call)
        WaitUntilVerified(jsMock, js => js.InvokeAsync<object>(
            "postVsMessage",
            It.Is<object[]>(args =>
                args.Length == 1
                && ((VsRequest)args[0]).Action == Shared.Contracts.BasicEnum.LogError
                && ((VsRequest)args[0]).Payload!.Contains("VsBridge")
                && ((VsRequest)args[0]).Payload.Contains("Request failed")
                && ((VsRequest)args[0]).Payload.Contains("pipe broken"))));
    }

    [Fact]
    public void LogWarning_PostsWithWarnLevel()
    {
        // Arrange
        var jsMock = new Mock<IJSRuntime>();
        jsMock
            .Setup(js => js.InvokeAsync<object>("postVsMessage", It.IsAny<object[]>()))
            .ReturnsAsync(new object());
        var logger = CreateLogger(jsMock);

        // Act
        logger.LogWarning("Approval", "Approval required");

        // Assert - fire-and-forget: verify via polling
        WaitUntilVerified(jsMock, js => js.InvokeAsync<object>(
            "postVsMessage",
            It.Is<object[]>(args =>
                args.Length == 1
                && ((VsRequest)args[0]).Action == Shared.Contracts.BasicEnum.LogError
                && ((VsRequest)args[0]).Payload!.Contains("WARN")
                && ((VsRequest)args[0]).Payload.Contains("Approval"))));
    }

    [Fact]
    public async Task Log_JsInteropThrows_DoesNotCrash()
    {
        // Arrange - bridge/JS unavailable: the logger must swallow, not throw
        var jsMock = new Mock<IJSRuntime>();
        jsMock
            .Setup(js => js.InvokeAsync<object>("postVsMessage", It.IsAny<object[]>()))
            .ThrowsAsync(new JSDisconnectedException("disconnected"));

        var logger = CreateLogger(jsMock);

        // Act - no exception escapes the synchronous call
        logger.LogError("Any", "message");

        // Give the fire-and-forget task a chance to run (and fail silently)
        await Task.Delay(50, TestContext.Current.CancellationToken);
        jsMock.Verify(js => js.InvokeAsync<object>("postVsMessage", It.IsAny<object[]>()), Times.Once);
    }
}
