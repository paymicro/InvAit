namespace ToolCore.Tests;

/// <summary>
/// Switch for the MCP integration tests that spawn real OS processes
/// (McpHost over a named pipe, stdio echo servers, an ASP.NET Core HTTP echo server).
/// <para>
/// Those tests are the only ones in the project that depend on process/pipe/port availability of the
/// host machine, so a stuck child process can hang the whole run instead of failing a single test.
/// Set <c>INVAIT_SKIP_INTEGRATION=1</c> to run the fast unit-only suite - the CI quality-gate step
/// does exactly that, and the integration suite runs afterwards in its own time-bounded step.
/// </para>
/// </summary>
internal static class IntegrationGate
{
    public const string EnvironmentVariable = "INVAIT_SKIP_INTEGRATION";

    /// <summary>True when <see cref="EnvironmentVariable"/> is set to <c>1</c>/<c>true</c>.</summary>
    public static bool IsDisabled
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return string.Equals(value, "1", StringComparison.Ordinal) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Marks the current test as skipped when integration tests are disabled.
    /// Must be called as the first statement of a test method (throws a skip exception).
    /// </summary>
    public static void SkipIfDisabled()
    {
        if (IsDisabled)
            Assert.Skip($"MCP integration tests are disabled by {EnvironmentVariable}.");
    }
}
