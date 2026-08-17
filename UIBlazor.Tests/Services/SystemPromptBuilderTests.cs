namespace UIBlazor.Tests.Services;

/// <summary>
/// <seealso cref="SystemPromptBuilder"/>
/// </summary>
public class SystemPromptBuilderTests
{
    private readonly Mock<IProfileManager> _profileManagerMock;
    private readonly Mock<IToolManager> _toolManagerMock;
    private readonly Mock<ISkillService> _skillServiceMock;
    private readonly Mock<IRuleService> _ruleServiceMock;
    private readonly Mock<IVsCodeContextService> _vsCodeContextServiceMock;

    public SystemPromptBuilderTests()
    {
        _profileManagerMock = new Mock<IProfileManager>();
        _toolManagerMock = new Mock<IToolManager>();
        _skillServiceMock = new Mock<ISkillService>();
        _ruleServiceMock = new Mock<IRuleService>();
        _vsCodeContextServiceMock = new Mock<IVsCodeContextService>();

        // Setup default profile with all prompt sections enabled
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
            SendModeInstructions = true
        });
    }

    private SystemPromptBuilder CreateBuilder()
    {
        return new SystemPromptBuilder(
            _profileManagerMock.Object,
            _toolManagerMock.Object,
            _skillServiceMock.Object,
            _ruleServiceMock.Object,
            _vsCodeContextServiceMock.Object
        );
    }

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
    public void BuildSolutionFiles_EmptyFileList_ReturnsEmptyString()
    {
        // Arrange
        var context = new VsCodeContext
        {
            SolutionPath = "B:\\TestSolution",
            SolutionFiles = []
        };
        var builder = CreateBuilder();

        // Act
        var result = builder.BuildSolutionFiles(context, true);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildSolutionFiles_WithDirectoriesAndFiles_FormatsCorrectly()
    {
        // Arrange
        var context = new VsCodeContext
        {
            SolutionPath = "B:\\TestSolution",
            SolutionFiles = [.. $"""
                Solution path: {VsCodeContext.DirPrefix} B:\TestSolution
                Project: ConsoleApp | B:\TestSolution\ConsoleApp\ConsoleApp.csproj
                {VsCodeContext.FilePrefix} B:\TestSolution\Readme.md
                {VsCodeContext.DirPrefix} B:\TestSolution\ConsoleApp\
                  {VsCodeContext.FilePrefix} B:\TestSolution\ConsoleApp\Program.cs
                  {VsCodeContext.FilePrefix} B:\TestSolution\ConsoleApp\Utils.cs
                {VsCodeContext.DirPrefix} B:\TestSolution\ConsoleApp\Ui\
                  {VsCodeContext.FilePrefix} B:\TestSolution\ConsoleApp\Ui\Test1.cs
                  {VsCodeContext.FilePrefix} B:\Other\ConsoleApp\Ui\Test2.cs
                Project: ConsoleApp.Tests | B:\TestSolution\ConsoleApp.Tests\ConsoleApp.Tests.csproj
                {VsCodeContext.DirPrefix} B:\TestSolution\ConsoleApp.Tests\
                  {VsCodeContext.FilePrefix} B:\TestSolution\ConsoleApp.Tests\UnitTest.cs
                """.Split('\n')]
        };
        var expected = $"""
            Solution path: {VsCodeContext.DirPrefix} B:\TestSolution
            Project: ConsoleApp | ConsoleApp\ConsoleApp.csproj
            {VsCodeContext.FilePrefix} Readme.md
            {VsCodeContext.DirPrefix} B:\TestSolution\ConsoleApp\
              {VsCodeContext.FilePrefix} Program.cs
              {VsCodeContext.FilePrefix} Utils.cs
            {VsCodeContext.DirPrefix} B:\TestSolution\ConsoleApp\Ui\
              {VsCodeContext.FilePrefix} Test1.cs
              {VsCodeContext.FilePrefix} B:\Other\ConsoleApp\Ui\Test2.cs
            Project: ConsoleApp.Tests | ConsoleApp.Tests\ConsoleApp.Tests.csproj
            {VsCodeContext.DirPrefix} B:\TestSolution\ConsoleApp.Tests\
              {VsCodeContext.FilePrefix} UnitTest.cs

            """;
        var builder = CreateBuilder();

        // Act
        var result = builder.BuildSolutionFiles(context, true);

        // Assert
        Assert.Equal(result, expected);
    }

    [Fact]
    public void BuildSolutionFiles_FilesWithoutDirectoryPrefix_AreRelativeToLastDirectory()
    {
        // Arrange
        var context = new VsCodeContext
        {
            SolutionPath = "B:\\TestSolution",
            SolutionFiles =
            [
                $"{VsCodeContext.DirPrefix} B:\\TestSolution\\src",
                "B:\\TestSolution\\src\\File1.cs",
                "B:\\TestSolution\\src\\File2.cs",
                $"{VsCodeContext.DirPrefix} B:\\TestSolution\\lib",
                "B:\\TestSolution\\lib\\Lib1.cs"
            ]
        };
        var builder = CreateBuilder();

        // Act
        var result = builder.BuildSolutionFiles(context, true);

        // Assert
        Assert.Contains("File1.cs", result);
        Assert.Contains("File2.cs", result);
        Assert.Contains("Lib1.cs", result);
        Assert.DoesNotContain("B:\\TestSolution\\src\\File1.cs", result);
        Assert.DoesNotContain("B:\\TestSolution\\lib\\Lib1.cs", result);
    }

    [Fact]
    public void Options_ReturnsActiveProfile()
    {
        // Arrange
        var expectedProfile = new ConnectionProfile { SystemPrompt = "Test" };
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(expectedProfile);
        var builder = CreateBuilder();

        // Act
        var result = builder.Options;

        // Assert
        Assert.Same(expectedProfile, result);
    }

    #region PrepareSubAgentSystemPromptAsync Tests

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

    #endregion
}
