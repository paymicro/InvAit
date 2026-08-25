namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for <see cref="UsageIndicators"/>
/// </summary>
public class UsageIndicatorsTests : BunitContext
{
    private readonly Mock<IChatService> _mockChatService;
    private readonly Mock<IProfileManager> _mockProfileManager;
    private readonly ConversationSession _session;
    private readonly ConnectionProfile _profile;

    public UsageIndicatorsTests()
    {
        _mockChatService = new Mock<IChatService>();
        _mockProfileManager = new Mock<IProfileManager>();

        _session = new ConversationSession { TotalTokens = 0 };
        _profile = new ConnectionProfile
        {
            TokensToCompress = 0,
            ContextWindow = 100_000
        };

        _mockChatService.Setup(x => x.Session).Returns(_session);
        _mockProfileManager.Setup(x => x.ActiveProfile).Returns(_profile);

        Services.AddSingleton(_mockChatService.Object);
        Services.AddSingleton(_mockProfileManager.Object);
        Services.AddRadzenComponents();
    }

    #region Rendering Tests

    [Fact]
    public void ShouldRenderContainer_AndContextWindowItem()
    {
        // Act
        var cut = Render<UsageIndicators>();

        // Assert
        Assert.NotNull(cut.Find(".usage-indicators"));
        var items = cut.FindAll(".usage-item");
        Assert.Single(items);
        Assert.Contains(SharedResource.ContextWindow, cut.Markup);
    }

    [Fact]
    public void ShouldNotRenderCompressionItem_WhenTokensToCompressIsZero()
    {
        // Arrange
        _profile.TokensToCompress = 0;

        // Act
        var cut = Render<UsageIndicators>();

        // Assert
        Assert.DoesNotContain(SharedResource.CompressionTokenThreshold, cut.Markup);
        Assert.Single(cut.FindAll(".usage-item"));
    }

    [Fact]
    public void ShouldRenderCompressionItem_WhenTokensToCompressIsPositive()
    {
        // Arrange
        _profile.TokensToCompress = 10_000;

        // Act
        var cut = Render<UsageIndicators>();

        // Assert
        Assert.Contains(SharedResource.CompressionTokenThreshold, cut.Markup);
        Assert.Equal(2, cut.FindAll(".usage-item").Count);
    }

    [Fact]
    public void ShouldRenderTokenCounts_InContextWindowItem()
    {
        // Arrange
        _session.TotalTokens = 25_000;
        _profile.ContextWindow = 100_000;

        // Act
        var cut = Render<UsageIndicators>();

        // Assert
        var item = cut.FindAll(".usage-item").Last();
        Assert.Contains("25000 / 100000", item.TextContent);
    }

    [Fact]
    public void ShouldShowZeroTokens_WhenSessionHasNoUsage()
    {
        // Arrange
        _session.TotalTokens = 0;

        // Act
        var cut = Render<UsageIndicators>();

        // Assert
        var item = cut.FindAll(".usage-item").Last();
        Assert.Contains("0 / 100000", item.TextContent);
    }

    #endregion

    #region Progress Bar Tests

    [Fact]
    public void TokenProgressBar_ReflectsRatio()
    {
        // Arrange
        _session.TotalTokens = 50_000;
        _profile.ContextWindow = 100_000;

        // Act
        var cut = Render<UsageIndicators>();

        // Assert
        var bar = cut.FindAll(".usage-bar").Single();
        Assert.Equal("width: 50%;", bar.GetAttribute("style"));
    }

    [Fact]
    public void TokenProgressBar_IsCappedAt100Percent()
    {
        // Arrange
        _session.TotalTokens = 150_000;
        _profile.ContextWindow = 100_000;

        // Act
        var cut = Render<UsageIndicators>();

        // Assert
        var bar = cut.FindAll(".usage-bar").Single();
        Assert.Equal("width: 100%;", bar.GetAttribute("style"));
    }

    [Fact]
    public void CompressionBar_ReflectsRatio()
    {
        // Arrange
        _profile.TokensToCompress = 20_000;
        _session.TotalTokens = 5_000;
        _profile.ContextWindow = 100_000;

        // Act
        var cut = Render<UsageIndicators>();

        // Assert - first item is compression
        var bars = cut.FindAll(".usage-bar");
        Assert.Equal(2, bars.Count);
        Assert.Equal("width: 25%;", bars[0].GetAttribute("style"));
        Assert.Equal("width: 5%;", bars[1].GetAttribute("style"));
    }

    #endregion

    #region Event Subscription Tests

    [Fact]
    public async Task SessionChangedEvent_TriggersRerender()
    {
        // Arrange
        var cut = Render<UsageIndicators>();
        _session.TotalTokens = 10_000;

        // Act - raise session changed (TotalTokens uses SetIfChanged which raises PropertyChanged on session,
        // but the component subscribes to IChatService.SessionChanged)
        await cut.InvokeAsync(() => _mockChatService.Raise(
            x => x.SessionChanged += null,
            new System.ComponentModel.PropertyChangedEventArgs(nameof(ConversationSession.TotalTokens))));

        // Assert - markup reflects updated tokens without errors
        Assert.Contains("10000 / 100000", cut.Markup);
    }

    [Fact]
    public async Task ProfilePropertyChangedEvent_TriggersRerender()
    {
        // Arrange
        var cut = Render<UsageIndicators>();
        _profile.ContextWindow = 200_000;

        // Act
        await cut.InvokeAsync(() => _mockProfileManager.Raise(
            x => x.PropertyChanged += null,
            new System.ComponentModel.PropertyChangedEventArgs(nameof(ConnectionProfile.ContextWindow))));

        // Assert
        Assert.Contains("0 / 200000", cut.Markup);
    }

    [Fact]
    public void Dispose_DoesNotThrow_AndUnsubscribes()
    {
        // Arrange
        var cut = Render<UsageIndicators>();

        // Act & Assert - raising events after dispose should not throw
        cut.Dispose();
        var exception = Record.Exception(() => _mockChatService.Raise(
            x => x.SessionChanged += null,
            new System.ComponentModel.PropertyChangedEventArgs(nameof(ConversationSession.TotalTokens))));
        Assert.Null(exception);
    }

    #endregion
}
