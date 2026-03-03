using Moq;
using Shared.Contracts;
using UIBlazor.Agents;
using UIBlazor.Services;
using UIBlazor.VS;

namespace UIBlazor.Tests.Agents;

public class BuiltInAgentTests
{
    private readonly Mock<IVsBridge> _vsBridgeMock;
    private readonly BuiltInAgent _agent;

    public BuiltInAgentTests()
    {
        _vsBridgeMock = new Mock<IVsBridge>();
        _agent = new BuiltInAgent(_vsBridgeMock.Object, Mock.Of<ISkillService>());
    }

    [Fact]
    public void Tools_AreRegistered()
    {
        Assert.NotEmpty(_agent.Tools);
        Assert.Contains(_agent.Tools, t => t.Name == BuiltInToolEnum.ReadFiles);
        Assert.Contains(_agent.Tools, t => t.Name == BuiltInToolEnum.CreateFile);
        Assert.Contains(_agent.Tools, t => t.Name == BuiltInToolEnum.ApplyDiff);
    }

    [Fact]
    public async Task Execute_ReadFiles_CallsBridge()
    {
        // Arrange
        var tool = _agent.Tools.First(t => t.Name == BuiltInToolEnum.ReadFiles);
        var args = new Dictionary<string, object> { { "path", "test.cs" } };

        // Act
        await tool.ExecuteAsync(args);

        // Assert
        _vsBridgeMock.Verify(b => b.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, args), Times.Once);
    }

    [Fact]
    public async Task Execute_ApplyDiff_CallsBridge()
    {
        // Arrange
        var tool = _agent.Tools.First(t => t.Name == BuiltInToolEnum.ApplyDiff);
        var args = new Dictionary<string, object> { { "path", "test.cs" } };

        // Act
        await tool.ExecuteAsync(args);

        // Assert
        _vsBridgeMock.Verify(b => b.ExecuteToolAsync(BuiltInToolEnum.ApplyDiff, args), Times.Once);
    }

    [Fact]
    public async Task Execute_Exec_CallsBridge()
    {
        // Arrange
        var tool = _agent.Tools.First(t => t.Name == BuiltInToolEnum.Exec);
        var args = new Dictionary<string, object> { { "command", "dir" } };

        // Act
        await tool.ExecuteAsync(args);

        // Assert
        _vsBridgeMock.Verify(b => b.ExecuteToolAsync(BuiltInToolEnum.Exec, args), Times.Once);
    }
}
