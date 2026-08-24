namespace UIBlazor.Tests.Services;

/// <summary>
/// <seealso cref="SystemPromptBuilder"/>
/// </summary>
public partial class SystemPromptBuilderTests
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

        // By default, delegate_task is available in Agent mode (like the main agent)
        _toolManagerMock
            .Setup(t => t.GetEnabledTools(AppMode.Agent))
            .Returns([new Tool { Name = BuiltInToolEnum.DelegateTask, Category = ToolCategory.SubAgent, NativeTool = null!, ExecuteAsync = null! }]);
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
}
