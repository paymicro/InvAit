using Shared.Contracts.McpHost;
using ToolCore.McpHost;

namespace ToolCore.Tests;

public class McpHostProtocolVersionGuardTests
{
    [Fact]
    public void CorrectVersion_Passes()
    {
        McpHostSupervisor.EnsureProtocolVersion(McpHostProtocol.Version);
    }

    [Fact]
    public void MismatchedVersion_ThrowsWithBothVersionsInMessage()
    {
        var ex = Assert.Throws<McpHostStartupException>(
            () => McpHostSupervisor.EnsureProtocolVersion("0.9"));

        Assert.Contains("0.9", ex.Message);
        Assert.Contains(McpHostProtocol.Version, ex.Message);
    }
}
