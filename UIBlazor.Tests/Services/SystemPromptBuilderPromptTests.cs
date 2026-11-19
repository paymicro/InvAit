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

        var context = new VsContext
        {
            SolutionPath = "B:\\TestSolution",
            ActiveFilePath = "B:\\TestSolution\\Program.cs",
            SelectionStartLine = 10,
            SelectionEndLine = 20,
            ActiveFileContent = "class Program { }",
            SolutionFiles =
            [
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

        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
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
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
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

        var context = new VsContext
        {
            SolutionPath = "B:\\TestSolution",
            ActiveFilePath = "B:\\TestSolution\\Program.cs",
            SelectionStartLine = 1,
            SelectionEndLine = 10,
            ActiveFileContent = "test content",
            SolutionFiles = ["B:\\TestSolution\\file1.cs", "B:\\TestSolution\\file2.cs"]
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

        var context = new VsContext
        {
            SolutionPath = "B:\\TestSolution",
            ActiveFilePath = "B:\\TestSolution\\Program.cs",
            SelectionStartLine = 1,
            SelectionEndLine = 10,
            ActiveFileContent = "test content",
            SolutionFiles = ["B:\\TestSolution\\file1.cs", "B:\\TestSolution\\file2.cs"]
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
        var context = new VsContext
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
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
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
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
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
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
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
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
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
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
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
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
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
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
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
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
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
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Chat, TestContext.Current.CancellationToken);

        // Assert — delegation instructions are NOT included in Chat mode
        Assert.Contains("Your current mode: Chat", result);
        Assert.DoesNotContain("Sub-Agent Delegation", result);
    }

    // ─── Prompt cache ───────────────────────────────────────────────────────────
    // The turn loop rebuilds the request on EVERY turn; the prompt must be assembled once
    // per (mode, delegate availability, revision) and reused verbatim in between, otherwise
    // a 40-turn run re-scans skills/rules/agents.md 40 times and busts the provider cache.

    [Fact]
    public async Task PrepareSystemPromptAsync_TwoConsecutiveCalls_SecondCallIsServedFromCache()
    {
        // Arrange — the skill scan is the expensive part; it must run exactly once.
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync("rules");
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync("agents");

        var builder = CreateBuilder();

        // Act
        var first = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);
        var second = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert — byte-identical text (the provider prompt cache stays warm) built from ONE scan.
        Assert.Equal(first, second);
        _skillServiceMock.Verify(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>()), Times.Once);
        _ruleServiceMock.Verify(r => r.GetRulesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _ruleServiceMock.Verify(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_ModeChanged_RebuildsPrompt()
    {
        // Arrange
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();

        // Act — a switch_mode between two turns must produce the NEW mode prompt.
        var agent = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);
        var plan = await builder.PrepareSystemPromptAsync(AppMode.Plan, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("Your current mode: Agent", agent);
        Assert.Contains("Your current mode: Plan", plan);
        Assert.Contains("PLANNING MODE", plan);
        Assert.DoesNotContain("PLANNING MODE", agent);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_ProfileChanged_RebuildsPrompt()
    {
        // Arrange — start with a distinctive prompt text, then swap the profile.
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();
        var before = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            SystemPrompt = "EDITED system prompt",
            SendCurrentDate = false,
            SendModeInstructions = true
        });
        _profileManagerMock.Raise(p => p.PropertyChanged += null, new System.ComponentModel.PropertyChangedEventArgs(nameof(IProfileManager.ActiveProfile)));

        // Act
        var after = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("Test system prompt from profile", before);
        Assert.Contains("EDITED system prompt", after);
        Assert.DoesNotContain("EDITED system prompt", before);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_ContextChanged_RebuildsPrompt()
    {
        // Arrange — the IDE pushes new context between turns (file switch).
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns(new VsContext
        {
            ActiveFilePath = "B:\\Before.cs",
            ActiveFileContent = "class Before { }",
            SolutionFiles = []
        });

        var builder = CreateBuilder();
        var before = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns(new VsContext
        {
            ActiveFilePath = "B:\\After.cs",
            ActiveFileContent = "class After { }",
            SolutionFiles = []
        });
        _vsCodeContextServiceMock.Raise(v => v.OnContextChanged += null);

        // Act
        var after = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("class Before { }", before);
        Assert.Contains("class After { }", after);
        Assert.DoesNotContain("class After { }", before);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_DelegateToolToggled_RebuildsPrompt()
    {
        // Arrange — delegate_task is available, so the delegation section is included.
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns((VsContext?)null);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);

        var builder = CreateBuilder();
        var before = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        // The user disables delegate_task: neither the profile nor the context changes, so only
        // the tool availability can invalidate the cached prompt.
        _toolManagerMock
            .Setup(t => t.GetEnabledTools(AppMode.Agent))
            .Returns([]);

        // Act
        var after = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("Sub-Agent Delegation", before);
        Assert.DoesNotContain("Sub-Agent Delegation", after);
    }
}
