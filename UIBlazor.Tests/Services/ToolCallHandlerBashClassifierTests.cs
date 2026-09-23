namespace UIBlazor.Tests.Services;

/// <summary>
/// Integration tests for bash command classification in <see cref="ToolCallHandler"/>.
/// Tests the mapping table: Category mode × Classification → Approval mode.
/// </summary>
public partial class ToolCallHandlerTests
{
    #region Category = Allow (trust more: safe + unknown auto-execute, destructive asks)

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryAllow_SafeCommand_Approved()
    {
        // Arrange
        SetupBashCategoryMode(ToolApprovalMode.Allow);

        var list = CreateBashToolCall("git status");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — safe command auto-approved
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
    }

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryAllow_UnknownCommand_Approved()
    {
        // Arrange
        SetupBashCategoryMode(ToolApprovalMode.Allow);

        var list = CreateBashToolCall("dotnet build");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — unknown command auto-approved (Allow = more trust)
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
    }

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryAllow_DestructiveCommand_Pending()
    {
        // Arrange
        SetupBashCategoryMode(ToolApprovalMode.Allow);

        var list = CreateBashToolCall("rm -rf /");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — destructive command asks (NOT auto-denied!)
        Assert.Equal(ToolApprovalStatus.Pending, list[0].ApprovalStatus);
    }

    #endregion

    #region Category = Ask (safe auto-execute, unknown + destructive ask)

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryAsk_SafeCommand_Approved()
    {
        // Arrange
        SetupBashCategoryMode(ToolApprovalMode.Ask);

        var list = CreateBashToolCall("git status");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — safe command auto-approved even in Ask mode
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
    }

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryAsk_UnknownCommand_Pending()
    {
        // Arrange
        SetupBashCategoryMode(ToolApprovalMode.Ask);

        var list = CreateBashToolCall("dotnet build");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — unknown command requires approval
        Assert.Equal(ToolApprovalStatus.Pending, list[0].ApprovalStatus);
    }

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryAsk_DestructiveCommand_Pending()
    {
        // Arrange
        SetupBashCategoryMode(ToolApprovalMode.Ask);

        var list = CreateBashToolCall("rm -rf /");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — destructive command asks (NOT auto-denied!)
        Assert.Equal(ToolApprovalStatus.Pending, list[0].ApprovalStatus);
    }

    #endregion

    #region Category = Deny (everything denied)

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryDeny_SafeCommand_Rejected()
    {
        // Arrange
        SetupBashCategoryMode(ToolApprovalMode.Deny);

        var list = CreateBashToolCall("git status");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — even safe commands denied
        Assert.Equal(ToolApprovalStatus.Rejected, list[0].ApprovalStatus);
    }

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryDeny_DestructiveCommand_Rejected()
    {
        // Arrange
        SetupBashCategoryMode(ToolApprovalMode.Deny);

        var list = CreateBashToolCall("rm -rf /");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — everything denied
        Assert.Equal(ToolApprovalStatus.Rejected, list[0].ApprovalStatus);
    }

    #endregion

    #region Command chains

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryAsk_Chain_SafeAndDestructive_Pending()
    {
        // Arrange
        SetupBashCategoryMode(ToolApprovalMode.Ask);

        var list = CreateBashToolCall("git status && rm -rf /");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — chain with destructive part → Ask (Pending)
        Assert.Equal(ToolApprovalStatus.Pending, list[0].ApprovalStatus);
    }

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryAllow_Chain_SafeAndSafe_Approved()
    {
        // Arrange
        SetupBashCategoryMode(ToolApprovalMode.Allow);

        var list = CreateBashToolCall("git status && git diff");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — chain of safe commands → Allow (Approved)
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
    }

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryAllow_Chain_SafeAndDestructive_Pending()
    {
        // Arrange
        SetupBashCategoryMode(ToolApprovalMode.Allow);

        var list = CreateBashToolCall("git status && rm -rf /");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — chain with destructive → Ask (Pending), NOT auto-denied
        Assert.Equal(ToolApprovalStatus.Pending, list[0].ApprovalStatus);
    }

    #endregion

    #region Custom patterns

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryAsk_CustomSafePattern_Approved()
    {
        // Arrange
        var toolSettings = new ToolSettings
        {
            BashSettings = new BashCommandSettings
            {
                AllowPatterns = ["^my-safe-tool(\\s|$)"]
            }
        };
        _toolManagerMock.Setup(t => t.Current).Returns(toolSettings);
        _toolManagerMock
            .Setup(t => t.GetApprovalModeByToolName(BuiltInToolEnum.Bash))
            .Returns(ToolApprovalMode.Ask);

        var list = CreateBashToolCall("my-safe-tool --flag");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — custom safe pattern auto-approved even in Ask mode
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
    }

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryAllow_CustomDenyPattern_Rejected()
    {
        // Arrange
        var toolSettings = new ToolSettings
        {
            BashSettings = new BashCommandSettings
            {
                DenyPatterns = ["dangerous-script"]
            }
        };
        _toolManagerMock.Setup(t => t.Current).Returns(toolSettings);
        _toolManagerMock
            .Setup(t => t.GetApprovalModeByToolName(BuiltInToolEnum.Bash))
            .Returns(ToolApprovalMode.Allow);

        var list = CreateBashToolCall("dangerous-script --arg");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — custom deny pattern → auto-rejected (NOT asked!)
        Assert.Equal(ToolApprovalStatus.Rejected, list[0].ApprovalStatus);
    }

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryAsk_CustomDenyPattern_Rejected()
    {
        // Arrange
        var toolSettings = new ToolSettings
        {
            BashSettings = new BashCommandSettings
            {
                DenyPatterns = ["forbidden-op"]
            }
        };
        _toolManagerMock.Setup(t => t.Current).Returns(toolSettings);
        _toolManagerMock
            .Setup(t => t.GetApprovalModeByToolName(BuiltInToolEnum.Bash))
            .Returns(ToolApprovalMode.Ask);

        var list = CreateBashToolCall("forbidden-op");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — custom deny pattern → auto-rejected even in Ask mode
        Assert.Equal(ToolApprovalStatus.Rejected, list[0].ApprovalStatus);
    }

    [Fact]
    public void PrepareToolsForApprovals_Bash_CategoryAllow_BuiltinDestructiveStill_Pending()
    {
        // Arrange — no custom deny patterns, only built-in destructive
        SetupBashCategoryMode(ToolApprovalMode.Allow);

        var list = CreateBashToolCall("rm -rf /");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — built-in destructive → Ask (Pending), NOT rejected
        Assert.Equal(ToolApprovalStatus.Pending, list[0].ApprovalStatus);
    }

    #endregion

    #region Null BashSettings (M3 fix — upgrade scenario)

    [Fact]
    public void PrepareToolsForApprovals_Bash_NullBashSettings_DoesNotCrash()
    {
        // Arrange — BashSettings is null (upgrade scenario from older version)
        var toolSettings = new ToolSettings { BashSettings = null! };
        _toolManagerMock.Setup(t => t.Current).Returns(toolSettings);
        _toolManagerMock
            .Setup(t => t.GetApprovalModeByToolName(BuiltInToolEnum.Bash))
            .Returns(ToolApprovalMode.Ask);

        var list = CreateBashToolCall("git status");

        // Act — should not throw NRE
        _sut.PrepareToolsForApprovals(list);

        // Assert — safe command still auto-approved even with null BashSettings
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
    }

    [Fact]
    public void PrepareToolsForApprovals_Bash_NullBashSettings_DestructiveStillPending()
    {
        // Arrange
        var toolSettings = new ToolSettings { BashSettings = null! };
        _toolManagerMock.Setup(t => t.Current).Returns(toolSettings);
        _toolManagerMock
            .Setup(t => t.GetApprovalModeByToolName(BuiltInToolEnum.Bash))
            .Returns(ToolApprovalMode.Ask);

        var list = CreateBashToolCall("rm -rf /");

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — built-in destructive patterns still work even with null BashSettings
        Assert.Equal(ToolApprovalStatus.Pending, list[0].ApprovalStatus);
    }

    #endregion

    #region Non-bash tools unaffected

    [Fact]
    public void PrepareToolsForApprovals_NonBashTool_UnaffectedByClassifier()
    {
        // Arrange
        _toolManagerMock.Setup(t => t.Current).Returns(new ToolSettings());
        _toolManagerMock
            .Setup(t => t.GetApprovalModeByToolName("read_files"))
            .Returns(ToolApprovalMode.Allow);

        var list = new List<ToolCall>
        {
            new()
            {
                Id = "1",
                Function = new ToolCallFunction
                {
                    Name = "read_files",
                    Arguments = """{"files": ["test.txt"]}"""
                }
            }
        };

        // Act
        _sut.PrepareToolsForApprovals(list);

        // Assert — non-bash tools use normal approval flow
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
    }

    #endregion

    #region Helpers

    private void SetupBashCategoryMode(ToolApprovalMode mode)
    {
        _toolManagerMock.Setup(t => t.Current).Returns(new ToolSettings());
        _toolManagerMock
            .Setup(t => t.GetApprovalModeByToolName(BuiltInToolEnum.Bash))
            .Returns(mode);
    }

    private static List<ToolCall> CreateBashToolCall(string command)
    {
        return
        [
            new ToolCall
            {
                Id = "1",
                Function = new ToolCallFunction
                {
                    Name = BuiltInToolEnum.Bash,
                    Arguments = $$"""{"command": "{{command}}"}"""
                }
            }
        ];
    }

    #endregion
}
