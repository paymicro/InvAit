using Moq;
using Shared.Contracts;
using UIBlazor.Agents;
using UIBlazor.Models;
using UIBlazor.Services;

namespace UIBlazor.Tests.Agents;

public class InternalExecutorTests
{
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly Mock<IChatService> _chatServiceMock;
    private readonly ConversationSession _session;
    private readonly InternalExecutor _executor;

    public InternalExecutorTests()
    {
        _session = new ConversationSession();
        _chatServiceMock = new Mock<IChatService>();
        _chatServiceMock.SetupGet(x => x.Session).Returns(_session);
        
        _serviceProviderMock = new Mock<IServiceProvider>();
        _serviceProviderMock
            .Setup(x => x.GetService(typeof(IChatService)))
            .Returns(_chatServiceMock.Object);
        _executor = new InternalExecutor(_serviceProviderMock.Object);
    }

    [Fact]
    public async Task ExecuteToolAsync_SwitchMode_WithValidMode_ReturnsSuccess()
    {
        // Arrange
        var args = new Dictionary<string, object> { { "param1", "Agent" } };

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
        var args = new Dictionary<string, object> { { "param1", "Chat" } };

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
        var args = new Dictionary<string, object> { { "param1", "Plan" } };

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.SwitchMode, args, CancellationToken.None    );

        // Assert
        Assert.True(result.Success);
        Assert.Contains("Plan", result.Result);
        Assert.Equal(AppMode.Plan, _session.Mode);
    }

    [Fact]
    public async Task ExecuteToolAsync_SwitchMode_WithCaseInsensitiveMode_ReturnsSuccess()
    {
        // Arrange
        var args = new Dictionary<string, object> { { "param1", "AGENT" } };

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
        var args = new Dictionary<string, object> { { "param1", "InvalidMode" } };

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
        var args = new Dictionary<string, object>();

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
        var args = new Dictionary<string, object> { { "wrongParam", "Agent" } };

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.SwitchMode, args, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Not supported mode", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteToolAsync_WithUnsupportedTool_ReturnsFailure()
    {
        // Arrange
        var args = new Dictionary<string, object>();

        // Act
        var result = await _executor.ExecuteToolAsync("unsupported_tool", args, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("unsupported_tool", result.ErrorMessage);
        Assert.Contains("Not supported tool", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteToolAsync_WithNullToolName_ReturnsFailure()
    {
        // Arrange
        var args = new Dictionary<string, object>();

        // Act
        var result = await _executor.ExecuteToolAsync(null, args, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Not supported tool", result.ErrorMessage);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("CHAT")]
    [InlineData("Chat")]
    [InlineData("agent")]
    [InlineData("AGENT")]
    [InlineData("Agent")]
    [InlineData("plan")]
    [InlineData("PLAN")]
    [InlineData("Plan")]
    public async Task ExecuteToolAsync_SwitchMode_AllModesCaseInsensitive(string modeValue)
    {
        // Arrange
        var args = new Dictionary<string, object> { { "param1", modeValue } };

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.SwitchMode, args, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Contains(modeValue.ToLowerInvariant(), result.Result.ToLowerInvariant());
    }
}
