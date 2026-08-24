namespace UIBlazor.Tests.Agents;

public partial class InternalExecutorTests
{
    [Fact]
    public async Task ExecuteToolAsync_SwitchMode_WithValidMode_ReturnsSuccess()
    {
        // Arrange
        var args = "{ \"mode\": \"Agent\" }";

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.SwitchMode, args, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("Agent", result.Result);
        Assert.Contains("successfully", result.Result);
        Assert.Equal(AppMode.Agent, _session.Mode);
    }

    [Fact]
    public async Task ExecuteToolAsync_SwitchMode_WithChatMode_ReturnsSuccess()
    {
        // Arrange
        _session.Mode = AppMode.Agent; // Start with different mode
        var args = JsonSerializer.Serialize(new { mode = "Chat" });

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.SwitchMode, args, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("Chat", result.Result);
        Assert.Equal(AppMode.Chat, _session.Mode);
    }

    [Fact]
    public async Task ExecuteToolAsync_SwitchMode_WithPlanMode_ReturnsSuccess()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { mode = "Plan" });

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.SwitchMode, args, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("Plan", result.Result);
        Assert.Equal(AppMode.Plan, _session.Mode);
    }

    [Fact]
    public async Task ExecuteToolAsync_SwitchMode_WithCaseInsensitiveMode_ReturnsSuccess()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { mode = "AGENT" });

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.SwitchMode, args, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(AppMode.Agent, _session.Mode);
    }

    [Fact]
    public async Task ExecuteToolAsync_SwitchMode_WithInvalidMode_ReturnsFailure()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { mode = "InvalidMode" });

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.SwitchMode, args, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Not supported mode", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteToolAsync_SwitchMode_WithNullArgs_ReturnsFailure()
    {
        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.SwitchMode, null, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Not supported mode", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteToolAsync_SwitchMode_WithEmptyArgs_ReturnsFailure()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new Dictionary<string, object>());

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.SwitchMode, args, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Not supported mode", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteToolAsync_SwitchMode_WithWrongParamName_ReturnsFailure()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { wrongParam = "Agent" });

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.SwitchMode, args, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Not supported mode", result.ErrorMessage);
    }
}
