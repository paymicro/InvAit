using System.Text.Json;
using Moq;
using Shared.Contracts;
using UIBlazor.Agents;
using UIBlazor.Services;
using UIBlazor.Services.Models;
using UIBlazor.VS;

namespace UIBlazor.Tests;

public class SkillServiceTests
{
    private readonly Mock<IVsBridge> _vsBridgeMock;
    private readonly SkillService _skillService;

    public SkillServiceTests()
    {
        _vsBridgeMock = new Mock<IVsBridge>();
        _skillService = new SkillService(_vsBridgeMock.Object);
    }

    [Fact]
    public async Task GetSkillsMetadataAsync_Success_ParsesAndCaches()
    {
        // Arrange
        var metadata = new List<Dictionary<string, string>>
        {
            new() { ["name"] = "Skill1", ["description"] = "Desc1", ["filePath"] = "path1" }
        };
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.GetSkillsMetadata, null))
            .ReturnsAsync(new VsToolResult { Success = true, Result = JsonSerializer.Serialize(metadata) });

        // Act
        var result1 = await _skillService.GetSkillsMetadataAsync();
        var result2 = await _skillService.GetSkillsMetadataAsync();

        // Assert
        Assert.Single(result1);
        Assert.Equal("Skill1", result1[0].Name);
        _vsBridgeMock.Verify(v => v.ExecuteToolAsync(BasicEnum.GetSkillsMetadata, null), Times.Once);
    }

    [Fact]
    public async Task LoadSkillContentAsync_Success_ParsesAndCaches()
    {
        // Arrange
        var content = new Dictionary<string, object>
        {
            ["name"] = "Skill1",
            ["description"] = "Desc1",
            ["content"] = "Full content",
            ["resources"] = new List<string> { "res1" }
        };
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadSkillContent, It.IsAny<Dictionary<string, object>>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = JsonSerializer.Serialize(content) });

        // Act
        var result1 = await _skillService.LoadSkillContentAsync("path1");
        var result2 = await _skillService.LoadSkillContentAsync("path1");

        // Assert
        Assert.NotNull(result1);
        Assert.Equal("Full content", result1.Content);
        Assert.Single(result1.Resources);
        _vsBridgeMock.Verify(v => v.ExecuteToolAsync(BasicEnum.ReadSkillContent, It.IsAny<Dictionary<string, object>>()), Times.Once);
    }

    [Fact]
    public void FormatSkillsForSystemPrompt_ReturnsFormattedString()
    {
        // Arrange
        var skills = new List<SkillMetadata>
        {
            new() { Name = "Skill1", Description = "Desc1", FilePath = "path1" }
        };

        // Act
        var result = _skillService.FormatSkillsForSystemPrompt(skills);

        // Assert
        Assert.Contains("## Available Skills", result);
        Assert.Contains("**Skill1**: Desc1", result);
        Assert.Contains("path1", result);
    }

    [Fact]
    public async Task RefreshCacheAsync_ClearsCachesAndReloadsMetadata()
    {
        // Arrange
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.GetSkillsMetadata, null))
            .ReturnsAsync(new VsToolResult { Success = true, Result = "[]" });

        // Act
        await _skillService.RefreshCacheAsync();

        // Assert
        _vsBridgeMock.Verify(v => v.ExecuteToolAsync(BasicEnum.GetSkillsMetadata, null), Times.Once);
    }
}
