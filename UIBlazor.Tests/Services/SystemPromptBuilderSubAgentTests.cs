namespace UIBlazor.Tests.Services;

/// <summary>
/// Tests for <see cref="SystemPromptBuilder.PrepareSubAgentSystemPromptAsync"/>.
/// </summary>
public partial class SystemPromptBuilderTests
{
    [Fact]
    public async Task PrepareSubAgentSystemPromptAsync_WithCustomPrompt_IncludesCustomPrompt()
    {
        // Arrange
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSubAgentSystemPromptAsync("You are a code reviewer.", TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("You are a code reviewer.", result);
    }

    [Fact]
    public async Task PrepareSubAgentSystemPromptAsync_WithEmptyPrompt_UsesDefaultPrompt()
    {
        // Arrange
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSubAgentSystemPromptAsync("", TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("You are a helpful assistant. Complete the task given to you.", result);
    }

    [Fact]
    public async Task PrepareSubAgentSystemPromptAsync_NeverIncludesMermaidInstructions()
    {
        // Arrange — profile has Mermaid enabled
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSubAgentSystemPromptAsync("Custom prompt", TestContext.Current.CancellationToken);

        // Assert — Mermaid is never included for sub-agents
        Assert.DoesNotContain("Use Mermaid diagrams", result);
    }

    [Fact]
    public async Task PrepareSubAgentSystemPromptAsync_NeverIncludesActiveFile()
    {
        // Arrange — context has active file
        var context = new VsCodeContext
        {
            SolutionPath = "B:\\TestSolution",
            ActiveFilePath = "B:\\TestSolution\\Program.cs",
            SelectionStartLine = 1,
            SelectionEndLine = 10,
            ActiveFileContent = "class Program { }",
            SolutionFiles = ["file1.cs", "file2.cs"]
        };
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns(context);
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSubAgentSystemPromptAsync("Custom prompt", TestContext.Current.CancellationToken);

        // Assert — active file is never included for sub-agents
        Assert.DoesNotContain("## Current (active) file", result);
        Assert.DoesNotContain("class Program { }", result);
    }

    [Fact]
    public async Task PrepareSubAgentSystemPromptAsync_IncludesSolutionStructure_WhenEnabled()
    {
        // Arrange
        var context = new VsCodeContext
        {
            SolutionPath = "B:\\TestSolution",
            ActiveFilePath = "B:\\TestSolution\\Program.cs",
            ActiveFileContent = "test",
            SolutionFiles = [$"  {VsCodeContext.DirPrefix} B:\\TestSolution\\src", "B:\\TestSolution\\src\\Program.cs"]
        };
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns(context);
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSubAgentSystemPromptAsync("Custom prompt", TestContext.Current.CancellationToken);

        // Assert — solution structure IS included
        Assert.Contains("# CURRENT CODE CONTEXT", result);
        Assert.Contains("Solution structure:", result);
    }

    [Fact]
    public async Task PrepareSubAgentSystemPromptAsync_IncludesRules_WhenEnabled()
    {
        // Arrange
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync("# Test Rules\nFollow these rules.");
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSubAgentSystemPromptAsync("Custom prompt", TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("# Test Rules", result);
        Assert.Contains("Follow these rules.", result);
    }

    [Fact]
    public async Task PrepareSubAgentSystemPromptAsync_IncludesSkills_WhenEnabled()
    {
        // Arrange
        var skillsMetadata = new List<SkillMetadata> { new() { Name = "TestSkill", Description = "A test skill" } };
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(skillsMetadata);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(skillsMetadata)).Returns("## Available Skills\n**TestSkill**: A test skill");
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSubAgentSystemPromptAsync("Custom prompt", TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("## Available Skills", result);
        Assert.Contains("**TestSkill**: A test skill", result);
    }

    [Fact]
    public async Task PrepareSubAgentSystemPromptAsync_IncludesModeInstructions_ForAgentMode()
    {
        // Arrange
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSubAgentSystemPromptAsync("Custom prompt", TestContext.Current.CancellationToken);

        // Assert — sub-agent always gets Agent mode instructions
        Assert.Contains("Your current mode: Agent", result);
    }

    [Fact]
    public async Task PrepareSubAgentSystemPromptAsync_DoesNotIncludeProfileSystemPrompt()
    {
        // Arrange — profile has its own system prompt
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSubAgentSystemPromptAsync("Custom sub-agent prompt", TestContext.Current.CancellationToken);

        // Assert — profile system prompt is NOT included (sub-agent uses its own custom prompt)
        Assert.DoesNotContain("Test system prompt from profile", result);
        Assert.Contains("Custom sub-agent prompt", result);
    }

    [Fact]
    public async Task PrepareSubAgentSystemPromptAsync_DoesNotIncludeDelegationInstructions()
    {
        // Arrange — delegate_task IS registered globally (default mock returns it),
        // but sub-agents must never get delegation instructions regardless.
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSubAgentSystemPromptAsync("Custom prompt", TestContext.Current.CancellationToken);

        // Assert — delegation instructions are NOT included for sub-agents even though
        // delegate_task is globally registered (it's excluded from sub-agent toolset by SubAgentExecutor)
        Assert.DoesNotContain("Sub-Agent Delegation", result);
        Assert.DoesNotContain("delegate_task", result);
    }
}
