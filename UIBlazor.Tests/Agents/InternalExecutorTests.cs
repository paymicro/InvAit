namespace UIBlazor.Tests.Agents;

public partial class InternalExecutorTests
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
    public async Task ExecuteToolAsync_WithUnsupportedTool_ReturnsFailure()
    {
        // Arrange
        var args = "{}";

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
        var args = "{}";

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
        var args = JsonSerializer.Serialize(new { mode = modeValue });

        // Act
        var result = await _executor.ExecuteToolAsync(BasicEnum.SwitchMode, args, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Contains(modeValue.ToLowerInvariant(), result.Result.ToLowerInvariant());
    }
}
