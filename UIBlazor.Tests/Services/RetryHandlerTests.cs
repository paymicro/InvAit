namespace UIBlazor.Tests.Services;

/// <summary>
/// Tests for <see cref="RetryHandler"/>.
/// </summary>
public class RetryHandlerTests
{
    private readonly RetryHandler _sut;

    public RetryHandlerTests()
    {
        _sut = new RetryHandler();
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(3, 10)]
    [InlineData(4, 20)]
    [InlineData(5, 20)]
    [InlineData(100, 20)]
    public void GetRetryDelay_VariousAttempts_ReturnsCorrectDelay(int attempt, int expectedDelay)
    {
        // Act
        var delay = _sut.GetRetryDelay(attempt);

        // Assert
        Assert.Equal(expectedDelay, delay);
    }

    [Fact]
    public async Task WaitForRetryAsync_CountsDown_CallbackCalledEachSecond()
    {
        // Arrange
        var countdownValues = new List<int>();
        var tcs = new TaskCompletionSource();

        // Act
        var task = _sut.WaitForRetryAsync(3, i => countdownValues.Add(i), CancellationToken.None);
        await task;

        // Assert
        Assert.Contains(3, countdownValues);
        Assert.Contains(2, countdownValues);
        Assert.Contains(1, countdownValues);
        Assert.Contains(0, countdownValues);
        Assert.Equal(4, countdownValues.Count);
    }

    [Fact]
    public async Task WaitForRetryAsync_Cancellation_ThrowsCancellationException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.WaitForRetryAsync(10, _ => { }, cts.Token));
        Assert.True(exception.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task WaitForRetryAsync_ZeroDelay_CallbackCalledOnceWithZero()
    {
        // Arrange
        var countdownValues = new List<int>();

        // Act
        await _sut.WaitForRetryAsync(0, i => countdownValues.Add(i), CancellationToken.None);

        // Assert
        Assert.Single(countdownValues);
        Assert.Equal(0, countdownValues[0]);
    }

    [Fact]
    public async Task WaitForRetryAsync_OneSecondDelay_CallbackCalledTwice()
    {
        // Arrange
        var countdownValues = new List<int>();

        // Act
        await _sut.WaitForRetryAsync(1, i => countdownValues.Add(i), CancellationToken.None);

        // Assert
        Assert.Equal(2, countdownValues.Count);
        Assert.Equal(1, countdownValues[0]);
        Assert.Equal(0, countdownValues[1]);
    }

    [Fact]
    public async Task WaitForRetryAsync_CallbackReceivesCurrentCountdown()
    {
        // Arrange
        var lastReceivedValue = -1;

        // Act
        await _sut.WaitForRetryAsync(2, i => lastReceivedValue = i, CancellationToken.None);

        // Assert
        Assert.Equal(0, lastReceivedValue);
    }
}
