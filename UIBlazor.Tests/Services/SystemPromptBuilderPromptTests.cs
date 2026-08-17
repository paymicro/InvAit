namespace UIBlazor.Tests.Services;

/// <summary>
/// Tests for <see cref="SystemPromptBuilder.PrepareSystemPromptAsync"/>.
/// </summary>
public partial class SystemPromptBuilderTests
{
    [Fact]
    public async Task PrepareSystemPromptAsync_AllComponentsPresent_BuildsCompletePrompt()
    {
        // Arrange
        var skillsMetadata = new List<SkillMetadata>
        {
            new() { Name = "TestSkill", Description = "A test skill" }
        };
        var rulesContent = "# Test Rules\nThese are test rules.";
        var agentsContent = "Agent instructions here";

        _skillServiceMock
            .Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(skillsMetadata);
        _skillServiceMock
            .Setup(s => s.FormatSkillsForSystemPrompt(skillsMetadata))
            .Returns("## Available Skills\n**TestSkill**: A test skill");

        var context = new VsCodeContext
        {
            SolutionPath = "B:\\TestSolution",
            ActiveFilePath = "B:\\TestSolution\\Program.cs",
            SelectionStartLine = 10,
            SelectionEndLine = 20,
            ActiveFileContent = "class Program { }",
            SolutionFiles =
            [
                $"  {VsCodeContext.DirPrefix} B:\\TestSolution\\src",
                "B:\\TestSolution\\src\\Program.cs"
            ]
        };
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns(context);

        _ruleServiceMock
            .Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(rulesContent);
        _ruleServiceMock
            .Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(agentsContent);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("Test system prompt from profile", result);
        Assert.Contains("Use Mermaid diagrams", result);
        Assert.Contains("Your current mode: Agent", result);
        Assert.Contains("## Available Skills", result);
        Assert.Contains("**TestSkill**: A test skill", result);
        Assert.Contains("# CURRENT CODE CONTEXT", result);
        Assert.Contains("Solution structure:", result);
        Assert.Contains("## Current (active) file", result);
        Assert.Contains("Path: B:\\TestSolution\\Program.cs", result);
        Assert.Contains("class Program { }", result);
        Assert.Contains("# Test Rules", result);
        Assert.Contains("Agent instructions here", result);
        Assert.Contains("Current date:", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_NoSkills_SkillsSectionExcluded()
    {
        // Arrange
        _skillServiceMock
            .Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _skillServiceMock
            .Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>()))
            .Returns(string.Empty);

        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Chat, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("Test system prompt from profile", result);
        Assert.Contains("Your current mode: Chat", result);
        Assert.Contains("Use Mermaid diagrams", result);
        Assert.DoesNotContain("## Available Skills", result);
        Assert.DoesNotContain("# CURRENT CODE CONTEXT", result);
        Assert.DoesNotContain("# Test Rules", result);
        Assert.DoesNotContain("Agent instructions here", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_NoCodeContext_ContextSectionExcluded()
    {
        // Arrange
        var skillsMetadata = new List<SkillMetadata> { new() { Name = "Skill1", Description = "Desc1" } };

        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(skillsMetadata);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(skillsMetadata)).Returns("## Available Skills\n**Skill1**: Desc1");
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("Test system prompt from profile", result);
        Assert.Contains("Your current mode: Agent", result);
        Assert.Contains("## Available Skills", result);
        Assert.DoesNotContain("# CURRENT CODE CONTEXT", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_SendSolutionStructureDisabled_StructureExcluded()
    {
        // Arrange
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            SystemPrompt = "Test system prompt from profile",
            SendCurrentFile = true,
            SendSolutionStructure = false,
            SendCurrentDate = true,
            UseMermaidDiagrams = true,
            SendRules = true,
            SendAgentsMd = true,
            SendSkills = true,
            SendModeInstructions = true
        });

        var context = new VsCodeContext
        {
            SolutionPath = "B:\\TestSolution",
            ActiveFilePath = "B:\\TestSolution\\Program.cs",
            SelectionStartLine = 1,
            SelectionEndLine = 10,
            ActiveFileContent = "test content",
            SolutionFiles = ["file1.cs", "file2.cs"]
        };
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns(context);

        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Chat, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("# CURRENT CODE CONTEXT", result);
        Assert.Contains("## Current (active) file", result);
        Assert.DoesNotContain("Solution structure:", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_SendCurrentFileDisabled_FileExcluded()
    {
        // Arrange
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            SystemPrompt = "Test system prompt from profile",
            SendCurrentFile = false,
            SendSolutionStructure = true,
            SendCurrentDate = true,
            UseMermaidDiagrams = true,
            SendRules = true,
            SendAgentsMd = true,
            SendSkills = true,
            SendModeInstructions = true
        });

        var context = new VsCodeContext
        {
            SolutionPath = "B:\\TestSolution",
            ActiveFilePath = "B:\\TestSolution\\Program.cs",
            SelectionStartLine = 1,
            SelectionEndLine = 10,
            ActiveFileContent = "test content",
            SolutionFiles = ["file1.cs", "file2.cs"]
        };
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns(context);

        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Chat, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("# CURRENT CODE CONTEXT", result);
        Assert.DoesNotContain("## Current (active) file", result);
        Assert.Contains("Solution structure:", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_EmptyActiveFilePath_FileExcluded()
    {
        // Arrange
        var context = new VsCodeContext
        {
            SolutionPath = "B:\\TestSolution",
            ActiveFilePath = string.Empty,
            SelectionStartLine = 0,
            SelectionEndLine = 0,
            ActiveFileContent = string.Empty,
            SolutionFiles = []
        };
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns(context);

        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Chat, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("## Current (active) file", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_NullAgentsMd_AgentsSectionExcluded()
    {
        // Arrange
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Chat, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("Agent instructions", result);
        Assert.DoesNotContain("# Agents instructions", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_SendRulesDisabled_RulesExcluded()
    {
        // Arrange
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            SystemPrompt = "Test system prompt from profile",
            SendCurrentFile = true,
            SendSolutionStructure = true,
            SendCurrentDate = true,
            UseMermaidDiagrams = true,
            SendRules = false,
            SendAgentsMd = true,
            SendSkills = true,
            SendModeInstructions = true
        });

        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync("# Test Rules\nThese are test rules.");
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Chat, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("# Test Rules", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_SendSkillsDisabled_SkillsSectionExcluded()
    {
        // Arrange
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            SystemPrompt = "Test system prompt from profile",
            SendCurrentFile = true,
            SendSolutionStructure = true,
            SendCurrentDate = true,
            UseMermaidDiagrams = true,
            SendRules = true,
            SendAgentsMd = true,
            SendSkills = false,
            SendModeInstructions = true
        });

        var skillsMetadata = new List<SkillMetadata>
        {
            new() { Name = "TestSkill", Description = "A test skill" }
        };
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(skillsMetadata);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(skillsMetadata)).Returns("## Available Skills\n**TestSkill**: A test skill");
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Chat, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("## Available Skills", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_SendModeInstructionsDisabled_ModeInstructionsExcluded_MermaidStillPresent()
    {
        // Arrange
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            SystemPrompt = "Test system prompt from profile",
            SendCurrentFile = true,
            SendSolutionStructure = true,
            SendCurrentDate = true,
            UseMermaidDiagrams = true,
            SendRules = true,
            SendAgentsMd = true,
            SendSkills = true,
            SendModeInstructions = false
        });

        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert — mode instructions excluded
        Assert.DoesNotContain("Your current mode:", result);
        Assert.DoesNotContain("Planning Mode Instructions", result);
        // Assert — Mermaid is still present (independent of mode instructions)
        Assert.Contains("Use Mermaid diagrams", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_UseMermaidDisabled_MermaidExcluded_ModeInstructionsStillPresent()
    {
        // Arrange
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            SystemPrompt = "Test system prompt from profile",
            SendCurrentFile = true,
            SendSolutionStructure = true,
            SendCurrentDate = true,
            UseMermaidDiagrams = false,
            SendRules = true,
            SendAgentsMd = true,
            SendSkills = true,
            SendModeInstructions = true
        });

        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert — Mermaid excluded
        Assert.DoesNotContain("Use Mermaid diagrams", result);
        // Assert — mode instructions still present (independent of Mermaid)
        Assert.Contains("Your current mode: Agent", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_PlanMode_IncludesPlanningInstructions()
    {
        // Arrange
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Plan, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("Your current mode: Plan", result);
        Assert.Contains("## Planning Mode Instructions", result);
        Assert.Contains("<plan>", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_DelegateTaskDisabled_DoesNotIncludeDelegationInstructions()
    {
        // Arrange — delegate_task is not available (user disabled it or category disabled)
        _toolManagerMock
            .Setup(t => t.GetEnabledTools(AppMode.Agent))
            .Returns([]); // no tools at all

        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert — delegation instructions are NOT included when delegate_task is unavailable
        Assert.Contains("Your current mode: Agent", result);
        Assert.DoesNotContain("Sub-Agent Delegation", result);
        Assert.DoesNotContain("delegate_task", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_DelegateTaskEnabled_IncludesDelegationInstructions()
    {
        // Arrange — delegate_task IS available (default setup in constructor)
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert — delegation instructions ARE included when delegate_task is available
        Assert.Contains("Your current mode: Agent", result);
        Assert.Contains("Sub-Agent Delegation", result);
        Assert.Contains("delegate_task", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_ChatMode_DoesNotIncludeDelegationInstructions()
    {
        // Arrange — in Chat mode, delegate_task is not available (SubAgent category not enabled in Chat mode)
        _toolManagerMock
            .Setup(t => t.GetEnabledTools(AppMode.Chat))
            .Returns([]);

        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsCodeContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Chat, TestContext.Current.CancellationToken);

        // Assert — delegation instructions are NOT included in Chat mode
        Assert.Contains("Your current mode: Chat", result);
        Assert.DoesNotContain("Sub-Agent Delegation", result);
    }
}
