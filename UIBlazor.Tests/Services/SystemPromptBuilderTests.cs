namespace UIBlazor.Tests.Services;

/// <summary>
/// <seealso cref="SystemPromptBuilder"/>
/// </summary>
public class SystemPromptBuilderTests
{
    private readonly Mock<ICommonSettingsProvider> _commonSettingsMock;
    private readonly Mock<IProfileManager> _profileManagerMock;
    private readonly Mock<IToolManager> _toolManagerMock;
    private readonly Mock<ISkillService> _skillServiceMock;
    private readonly Mock<IRuleService> _ruleServiceMock;
    private readonly Mock<IVsCodeContextService> _vsCodeContextServiceMock;

    public SystemPromptBuilderTests()
    {
        _commonSettingsMock = new Mock<ICommonSettingsProvider>();
        _profileManagerMock = new Mock<IProfileManager>();
        _toolManagerMock = new Mock<IToolManager>();
        _skillServiceMock = new Mock<ISkillService>();
        _ruleServiceMock = new Mock<IRuleService>();
        _vsCodeContextServiceMock = new Mock<IVsCodeContextService>();

        // Setup default profile
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            SystemPrompt = "Test system prompt from profile"
        });

        // Setup default common settings
        _commonSettingsMock.SetupGet(c => c.Current).Returns(new CommonOptions
        {
            SendSolutionsStricture = true,
            SendCurrentFile = true
        });
    }

    private SystemPromptBuilder CreateBuilder()
    {
        return new SystemPromptBuilder(
            _commonSettingsMock.Object,
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

        _toolManagerMock
            .Setup(t => t.GetToolUseSystemInstructions(It.IsAny<AppMode>(), It.IsAny<bool>()))
            .Returns("## Tool Usage Instructions\nUse tools wisely.");

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("Test system prompt from profile", result);
        Assert.Contains("## Tool Usage Instructions", result);
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
        _toolManagerMock.Setup(t => t.GetToolUseSystemInstructions(It.IsAny<AppMode>(), false)).Returns("Tool instructions");

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Chat, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("Test system prompt from profile", result);
        Assert.Contains("Tool instructions", result);
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
        _toolManagerMock.Setup(t => t.GetToolUseSystemInstructions(It.IsAny<AppMode>(), true)).Returns("Tool instructions");

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("Test system prompt from profile", result);
        Assert.Contains("Tool instructions", result);
        Assert.Contains("## Available Skills", result);
        Assert.DoesNotContain("# CURRENT CODE CONTEXT", result);
    }

    [Fact]
    public async Task PrepareSystemPromptAsync_SendSolutionsStructureDisabled_StructureExcluded()
    {
        // Arrange
        _commonSettingsMock.SetupGet(c => c.Current).Returns(new CommonOptions
        {
            SendSolutionsStricture = false,
            SendCurrentFile = true
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
        _toolManagerMock.Setup(t => t.GetToolUseSystemInstructions(It.IsAny<AppMode>(), false)).Returns(string.Empty);

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
        _commonSettingsMock.SetupGet(c => c.Current).Returns(new CommonOptions
        {
            SendSolutionsStricture = true,
            SendCurrentFile = false
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
        _toolManagerMock.Setup(t => t.GetToolUseSystemInstructions(It.IsAny<AppMode>(), false)).Returns(string.Empty);

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
            SolutionFiles = new List<string>()
        };
        _vsCodeContextServiceMock.SetupGet(v => v.CurrentContext).Returns(context);

        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _skillServiceMock.Setup(s => s.FormatSkillsForSystemPrompt(It.IsAny<List<SkillMetadata>>())).Returns(string.Empty);
        _ruleServiceMock.Setup(r => r.GetRulesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _ruleServiceMock.Setup(r => r.GetAgentsMdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(string.Empty);
        _toolManagerMock.Setup(t => t.GetToolUseSystemInstructions(It.IsAny<AppMode>(), false)).Returns(string.Empty);

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
        _toolManagerMock.Setup(t => t.GetToolUseSystemInstructions(It.IsAny<AppMode>(), false)).Returns(string.Empty);

        var builder = CreateBuilder();

        // Act
        var result = await builder.PrepareSystemPromptAsync(AppMode.Chat, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("Agent instructions", result);
        Assert.DoesNotContain("# Agents instructions", result);
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
            SolutionFiles = new List<string>
            {
                $"{VsCodeContext.DirPrefix} B:\\TestSolution\\src",
                "B:\\TestSolution\\src\\File1.cs",
                "B:\\TestSolution\\src\\File2.cs",
                $"{VsCodeContext.DirPrefix} B:\\TestSolution\\lib",
                "B:\\TestSolution\\lib\\Lib1.cs"
            }
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
}
