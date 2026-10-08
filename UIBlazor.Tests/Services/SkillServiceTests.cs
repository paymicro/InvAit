namespace UIBlazor.Tests.Services;

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
    public async Task GetSkillsMetadataAsync_WhenVsBridgeFails_ReturnsEmptyList()
    {
        // Arrange
        _vsBridgeMock
            .Setup(x => x.ExecuteToolAsync(BasicEnum.GetSkillsMetadata, null))
            .ReturnsAsync(new VsToolResult { Success = false });

        // Act
        var result = await _skillService.GetSkillsMetadataAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSkillsMetadataAsync_WhenVsBridgeSucceeds_ParsesAndCachesResult()
    {
        // Arrange
        var jsonResponse = @"[
            { ""name"": ""TestSkill"", ""description"": ""A test skill"" },
            { ""name"": ""Skill2"", ""description"": ""Second skill"" }
        ]";

        _vsBridgeMock
            .Setup(x => x.ExecuteToolAsync(BasicEnum.GetSkillsMetadata, null))
            .ReturnsAsync(new VsToolResult { Success = true, Result = jsonResponse });

        // Act 1 - Initial fetch
        var result1 = await _skillService.GetSkillsMetadataAsync(CancellationToken.None);

        // Assert 1
        Assert.NotNull(result1);
        Assert.Equal(2, result1.Count);
        Assert.Equal("TestSkill", result1[0].Name);
        Assert.Equal("A test skill", result1[0].Description);

        // Act 2 - Should use cache
        var result2 = await _skillService.GetSkillsMetadataAsync(CancellationToken.None);

        // Assert 2
        Assert.Same(result1, result2); // Must be the exact same list instance due to caching
        _vsBridgeMock.Verify(x => x.ExecuteToolAsync(BasicEnum.GetSkillsMetadata, null), Times.Once); // Should only be called once
    }

    [Fact]
    public async Task GetSkillsMetadataAsync_WhenJsonIsInvalid_ReturnsEmptyList()
    {
        // Arrange
        _vsBridgeMock
            .Setup(x => x.ExecuteToolAsync(BasicEnum.GetSkillsMetadata, null))
            .ReturnsAsync(new VsToolResult { Success = true, Result = "invalid json" });

        // Act
        var result = await _skillService.GetSkillsMetadataAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadSkillContentAsync_WhenVsBridgeFails_ReturnsNull()
    {
        // Arrange
        _vsBridgeMock
            .Setup(x => x.ExecuteToolAsync(BasicEnum.ReadSkillContent, It.IsAny<string>()))
            .ReturnsAsync(new VsToolResult { Success = false });

        // Act
        var result = await _skillService.LoadSkillContentAsync("coolSkill", CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadSkillContentAsync_WhenVsBridgeSucceeds_ParsesAndCachesResult()
    {
        // Arrange
        var filePath = "test/success.md";
        var jsonResponse = @"{
            ""name"": ""CoolSkill"",
            ""description"": ""Desc"",
            ""content"": ""# Content""
        }";

        _vsBridgeMock
            .Setup(x => x.ExecuteToolAsync(BasicEnum.ReadSkillContent, It.Is<string>(s => s.Contains(filePath))))
            .ReturnsAsync(new VsToolResult { Success = true, Result = jsonResponse });

        // Act 1
        var result1 = await _skillService.LoadSkillContentAsync(filePath, CancellationToken.None);

        // Assert 1
        Assert.NotNull(result1);
        Assert.Equal("CoolSkill", result1.Name);
        Assert.Equal("Desc", result1.Description);
        Assert.Equal("# Content", result1.Content);

        // Act 2 - Should load from cache
        var result2 = await _skillService.LoadSkillContentAsync(filePath, CancellationToken.None);

        // Assert 2
        Assert.Same(result1, result2);
        _vsBridgeMock.Verify(x => x.ExecuteToolAsync(BasicEnum.ReadSkillContent, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task LoadSkillContentAsync_CacheEviction_LimitsTo10Items()
    {
        // Arrange
        for (var i = 0; i < 11; i++)
        {
            var i1 = i;
            _vsBridgeMock
                .Setup(x => x.ExecuteToolAsync(BasicEnum.ReadSkillContent, It.Is<string>(s => s.Contains($"Skill{i1}"))))
                .ReturnsAsync(new VsToolResult { Success = true, Result = $@"{{ ""name"": ""Skill{i1}"", ""description"": ""skill desc"", ""content"": ""no"", ""resources"": [] }}" });
        }

        // Act - Load 11 items
        for (var i = 0; i < 11; i++)
        {
            await _skillService.LoadSkillContentAsync($"Skill{i}", CancellationToken.None);
        }

        var resultReloaded = await _skillService.LoadSkillContentAsync("Skill0", CancellationToken.None);
        var result1 = await _skillService.LoadSkillContentAsync("Skill1", CancellationToken.None);

        // Assert
        Assert.NotNull(resultReloaded);
        _vsBridgeMock.Verify(x => x.ExecuteToolAsync(BasicEnum.ReadSkillContent, It.Is<string>(s => s.Equals("{\"skillName\":\"Skill1\"}"))), Times.Once);
        _vsBridgeMock.Verify(x => x.ExecuteToolAsync(BasicEnum.ReadSkillContent, It.Is<string>(s => s.Equals("{\"skillName\":\"Skill0\"}"))), Times.Exactly(2));
        Assert.NotNull(result1);
    }

    [Fact]
    public void FormatSkillsForSystemPrompt_EmptyList_ReturnsEmptyString()
    {
        // Arrange
        var skills = new List<SkillMetadata>();

        // Act
        var result = _skillService.FormatSkillsForSystemPrompt(skills);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatSkillsForSystemPrompt_ValidList_FormatsCorrectly()
    {
        // Arrange
        var skills = new List<SkillMetadata>
        {
            new() { Name = "SkillA", Description = "DescA" }
        };

        // Act
        var result = _skillService.FormatSkillsForSystemPrompt(skills);

        // Assert
        Assert.Contains("## Available Skills", result);
        Assert.Contains("**SkillA**: DescA", result);
        Assert.Contains(BasicEnum.ReadSkillContent, result);
        // Should NOT contain the old read_skill_reference tool name
        Assert.DoesNotContain("read_skill_reference", result);
        // Should mention the optional fileName parameter
        Assert.Contains("fileName", result);
    }

    [Fact]
    public async Task LoadSkillContentMarkDownAsync_WithFileName_CallsReadSkillContent()
    {
        // Arrange — when fileName is provided, the service should call ReadSkillContent
        // (not ReadSkillReference which no longer exists)
        var rawFileContent = "Raw reference file content";
        _vsBridgeMock
            .Setup(x => x.ExecuteToolAsync(BasicEnum.ReadSkillContent, It.Is<string>(s => s.Contains("\"fileName\":")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = rawFileContent });

        // Act
        var args = JsonUtils.SerializeCompact(new { skillName = "test-skill", fileName = "references/api.md" });
        var result = await _skillService.LoadSkillContentMarkDownAsync(args, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Contains(rawFileContent, result.Result);
        Assert.Contains("references/api.md", result.Result);
        // Verify ReadSkillContent was called (not ReadSkillReference)
        _vsBridgeMock.Verify(
            x => x.ExecuteToolAsync(BasicEnum.ReadSkillContent, It.Is<string>(s => s.Contains("fileName")), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task LoadSkillContentMarkDownAsync_WithoutFileName_LoadsSkillContent()
    {
        // Arrange — when fileName is NOT provided, should load SKILL.md content via cache
        var jsonResponse = JsonUtils.Serialize(new SkillContent
        {
            Name = "TestSkill",
            Description = "Desc",
            Content = "# Skill Content",
            Files = ["references/api.md", "scripts/build.sh"]
        });

        _vsBridgeMock
            .Setup(x => x.ExecuteToolAsync(BasicEnum.ReadSkillContent, It.Is<string>(s => !s.Contains("fileName")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = jsonResponse });

        // Act
        var args = JsonUtils.SerializeCompact(new { skillName = "TestSkill" });
        var result = await _skillService.LoadSkillContentMarkDownAsync(args, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("# Skill Content", result.Result);
        // Should list available files
        Assert.Contains("references/api.md", result.Result);
        Assert.Contains("scripts/build.sh", result.Result);
    }

    [Fact]
    public async Task LoadSkillContentMarkDownAsync_WithMissingSkillName_ReturnsError()
    {
        // Arrange
        var args = "{}";

        // Act
        var result = await _skillService.LoadSkillContentMarkDownAsync(args, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Skill name is missing", result.ErrorMessage);
    }

    [Fact]
    public async Task RefreshCacheAsync_ClearsBothCaches()
    {
        // Arrange
        _vsBridgeMock
            .Setup(x => x.ExecuteToolAsync(BasicEnum.GetSkillsMetadata, null))
            .ReturnsAsync(new VsToolResult { Success = true, Result = @"[{ ""name"": ""MetadataSkill"" }]" });

        _vsBridgeMock
            .Setup(x => x.ExecuteToolAsync(BasicEnum.ReadSkillContent, It.IsAny<string>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = @"{ ""name"": ""ContentSkill"" }" });

        // Populate both caches
        await _skillService.GetSkillsMetadataAsync(CancellationToken.None); // Cache populated 1
        await _skillService.LoadSkillContentAsync("path1", CancellationToken.None); // Cache populated 2

        // Reset mock counts
        _vsBridgeMock.Invocations.Clear();

        // Act
        await _skillService.RefreshCacheAsync(CancellationToken.None);

        // Refresh calls GetSkillsMetadataAsync internally once
        _vsBridgeMock.Verify(x => x.ExecuteToolAsync(BasicEnum.GetSkillsMetadata, null), Times.Once);

        // Request content again to verify cache was cleared
        await _skillService.LoadSkillContentAsync("path1", CancellationToken.None);
        _vsBridgeMock.Verify(x => x.ExecuteToolAsync(BasicEnum.ReadSkillContent, It.IsAny<string>()), Times.Once);
    }
}
