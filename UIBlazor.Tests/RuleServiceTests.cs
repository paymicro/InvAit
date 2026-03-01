using Moq;
using Shared.Contracts;
using UIBlazor.Agents;
using UIBlazor.Services;
using UIBlazor.VS;

namespace UIBlazor.Tests;

public class RuleServiceTests
{
    private readonly Mock<IVsBridge> _vsBridgeMock;
    private readonly RuleService _ruleService;

    public RuleServiceTests()
    {
        _vsBridgeMock = new Mock<IVsBridge>();
        _ruleService = new RuleService(_vsBridgeMock.Object);
    }

    [Fact]
    public async Task GetRulesAsync_FirstCall_CallsVsBridgeAndCaches()
    {
        // Arrange
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.GetRules, null))
            .ReturnsAsync(new VsToolResult { Success = true, Result = "Test Rules" });

        // Act
        var result1 = await _ruleService.GetRulesAsync();
        var result2 = await _ruleService.GetRulesAsync();

        // Assert
        Assert.Equal("Test Rules", result1);
        Assert.Equal("Test Rules", result2);
        _vsBridgeMock.Verify(v => v.ExecuteToolAsync(BasicEnum.GetRules, null), Times.Once);
    }

    [Fact]
    public async Task GetRulesAsync_BridgeFailure_ReturnsCachedOrEmpty()
    {
        // Arrange
        _vsBridgeMock.SetupSequence(v => v.ExecuteToolAsync(BasicEnum.GetRules, null))
            .ReturnsAsync(new VsToolResult { Success = true, Result = "Old Rules" })
            .ReturnsAsync(new VsToolResult { Success = false, ErrorMessage = "Error" });

        // Act
        await _ruleService.GetRulesAsync(); // Cache "Old Rules"
        _ruleService.RefreshCacheAsync();   // Reset cache update time (or just force refresh by clearing)
        // Note: RefreshCacheAsync in RuleService.cs clears _rulesCache = null;
        
        var result = await _ruleService.GetRulesAsync();

        // Assert
        Assert.Equal(string.Empty, result); // Because cache was cleared by RefreshCacheAsync
    }

    [Fact]
    public async Task RefreshCacheAsync_ClearsPreviousRules()
    {
        // Arrange
        _vsBridgeMock.SetupSequence(v => v.ExecuteToolAsync(BasicEnum.GetRules, null))
            .ReturnsAsync(new VsToolResult { Success = true, Result = "Rules v1" })
            .ReturnsAsync(new VsToolResult { Success = true, Result = "Rules v2" });

        // Act
        await _ruleService.GetRulesAsync();
        await _ruleService.RefreshCacheAsync();
        var result = await _ruleService.GetRulesAsync();

        // Assert
        Assert.Equal("Rules v2", result);
        _vsBridgeMock.Verify(v => v.ExecuteToolAsync(BasicEnum.GetRules, null), Times.Exactly(2));
    }
}
