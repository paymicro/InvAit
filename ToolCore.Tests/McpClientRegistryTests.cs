using System.Text.Json;
using McpHost;
using Shared.Contracts.McpHost;
using ToolCore.Tests.TestAssets;

namespace ToolCore.Tests;

public class McpClientRegistryIntegrationTests
{
    private static string GetEchoServerExePath()
        => TestAssetLocator.GetAssetExePath("EchoMcpServer", "EchoMcpServer.exe");

    [Fact]
    public async Task ListTools_ReturnsEchoServerTools()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            var tools = (await registry.ListToolsAsync(new McpListToolsParams
            {
                ServerId = "echo-test",
                Command = GetEchoServerExePath(),
            }, CancellationToken.None)).GetProperty("tools");

            var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();

            Assert.Contains("echo", names);
            Assert.Contains("add", names);
            Assert.Contains("fail", names);

            var echo = tools.EnumerateArray().First(t => t.GetProperty("name").GetString() == "echo");
            Assert.True(echo.TryGetProperty("inputSchema", out _));
        }
    }

    [Fact]
    public async Task CallTool_Echo_ReturnsTextContent()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            var result = await registry.CallToolAsync(new McpCallToolParams
            {
                ServerId = "echo-test",
                Command = GetEchoServerExePath(),
                ToolName = "echo",
                Arguments = JsonSerializer.SerializeToElement(new { text = "world" }),
            }, CancellationToken.None);

            Assert.False(result.GetProperty("isError").GetBoolean());
            var content = result.GetProperty("content");
            Assert.Equal(1, content.GetArrayLength());
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.Equal("echo: world", content[0].GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task CallTool_Fail_ReturnsIsError()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            var result = await registry.CallToolAsync(new McpCallToolParams
            {
                ServerId = "echo-test",
                Command = GetEchoServerExePath(),
                ToolName = "fail",
                Arguments = JsonSerializer.SerializeToElement(new { }),
            }, CancellationToken.None);

            Assert.True(result.GetProperty("isError").GetBoolean());
        }
    }

    [Fact]
    public async Task CallTool_UnknownTool_ReturnsErrorResult()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            var result = await registry.CallToolAsync(new McpCallToolParams
            {
                ServerId = "echo-test",
                Command = GetEchoServerExePath(),
                ToolName = "no_such_tool",
            }, CancellationToken.None);

            Assert.True(result.GetProperty("isError").GetBoolean());
        }
    }

    [Fact]
    public async Task Environment_Sanitized_AllowlistKept_SecretsDropped()
    {
        Environment.SetEnvironmentVariable("INVGEN_TEST_SECRET", "top-secret", EnvironmentVariableTarget.Process);

        try
        {
            var registry = new McpClientRegistry();
            await using (registry)
            {
                var pathValue = await CallGetEnv(registry, "PATH");
                var secretValue = await CallGetEnv(registry, "INVGEN_TEST_SECRET");

                Assert.NotEqual("<unset>", pathValue);
                Assert.Equal("<unset>", secretValue);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("INVGEN_TEST_SECRET", null, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public async Task UserEnv_OverridesDefaults()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            var result = await registry.CallToolAsync(new McpCallToolParams
            {
                ServerId = "echo-env-override",
                Command = GetEchoServerExePath(),
                ToolName = "get_env",
                Arguments = JsonSerializer.SerializeToElement(new { name = "ECHO_CUSTOM_VAR" }),
                Env = new Dictionary<string, string> { ["ECHO_CUSTOM_VAR"] = "custom-value" },
            }, CancellationToken.None);

            var text = result.GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Equal("custom-value", text);
        }
    }

    [Fact]
    public async Task StopAll_DisposesServers_NextCallRestarts()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            var launch = new McpListToolsParams { ServerId = "echo-restart", Command = GetEchoServerExePath() };
            await registry.ListToolsAsync(launch, CancellationToken.None);

            var stopped = await registry.StopAllAsync();
            Assert.Equal(1, stopped);
            Assert.Empty(registry.DescribeServers());

            var tools = (await registry.ListToolsAsync(launch, CancellationToken.None)).GetProperty("tools");
            Assert.True(tools.GetArrayLength() >= 3);
        }
    }

    [Fact]
    public async Task UnknownCommand_ListTools_ThrowsFileNotFound()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => registry.ListToolsAsync(new McpListToolsParams
            {
                ServerId = "ghost",
                Command = "definitely-not-a-real-command-xyz",
            }, CancellationToken.None));
        }
    }

    [Fact]
    public async Task LaunchParamsChanged_SameServerId_ClientRestarts()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            var v1 = await CallGetEnvWith(registry, "stale-server", new Dictionary<string, string> { ["ECHO_PROBE"] = "v1" }, "ECHO_PROBE");
            Assert.Equal("v1", v1);

            var v2 = await CallGetEnvWith(registry, "stale-server", new Dictionary<string, string> { ["ECHO_PROBE"] = "v2" }, "ECHO_PROBE");

            Assert.Equal("v2", v2);
        }
    }

    [Fact]
    public async Task SpawnArgs_PassedAsArray_SpacesPreservedWithoutSplitter()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            var tools = (await registry.ListToolsAsync(new McpListToolsParams
            {
                ServerId = "spawn-args",
                Command = GetEchoServerExePath(),
                Args = ["--probe", "hello world", "x"],
            }, CancellationToken.None)).GetProperty("tools");
            Assert.Contains("spawn_args", tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()));

            var result = await registry.CallToolAsync(new McpCallToolParams
            {
                ServerId = "spawn-args",
                Command = GetEchoServerExePath(),
                Args = ["--probe", "hello world", "x"],
                ToolName = "spawn_args",
            }, CancellationToken.None);

            Assert.False(result.GetProperty("isError").GetBoolean());
            var text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
            Assert.Contains("hello world|x", text);
        }
    }

    [Fact]
    public async Task ListTools_EmptyServerId_ThrowsArgumentException()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => registry.ListToolsAsync(
                new McpListToolsParams { ServerId = "" }, CancellationToken.None));
        }
    }

    [Fact]
    public async Task ListTools_MissingCommandAndUrl_ThrowsArgumentException()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => registry.ListToolsAsync(
                new McpListToolsParams { ServerId = "no-launch-info" }, CancellationToken.None));
        }
    }

    [Fact]
    public async Task CallTool_MissingToolName_ThrowsArgumentException()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => registry.CallToolAsync(
                new McpCallToolParams
                {
                    ServerId = "s",
                    Command = GetEchoServerExePath(),
                    ToolName = "",
                }, CancellationToken.None));
        }
    }

    [Fact]
    public async Task KilledChildProcess_AutoRecovers_OnNextCall()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            var marker = "m-" + Guid.NewGuid().ToString("N")[..8];
            var launch = new McpCallToolParams
            {
                ServerId = "recover",
                Command = GetEchoServerExePath(),
                ToolName = "spawn_args",
                Args = ["--marker", marker],
            };

            var first = await registry.CallToolAsync(launch, CancellationToken.None);
            Assert.Contains(marker, Text(first));

            var exitCall = new McpCallToolParams
            {
                ServerId = launch.ServerId,
                Command = launch.Command,
                Args = launch.Args,
                ToolName = "exit",
            };
            await Assert.ThrowsAnyAsync<Exception>(
                () => registry.CallToolAsync(exitCall, CancellationToken.None));

            var third = await registry.CallToolAsync(launch, CancellationToken.None);
            Assert.False(third.GetProperty("isError").GetBoolean());
            Assert.Contains(marker, Text(third));

            var tools = (await registry.ListToolsAsync(new McpListToolsParams
            {
                ServerId = launch.ServerId,
                Command = launch.Command,
                Args = launch.Args,
            }, CancellationToken.None)).GetProperty("tools");
            Assert.True(tools.GetArrayLength() >= 5);
        }
    }

    [Fact]
    public async Task CallTool_Cyrillic_RoundTripsUnescaped()
    {
        var registry = new McpClientRegistry();
        await using (registry)
        {
            var result = await registry.CallToolAsync(new McpCallToolParams
            {
                ServerId = "cyr-test",
                Command = GetEchoServerExePath(),
                ToolName = "cyr",
                Arguments = JsonSerializer.SerializeToElement(new { text = "мир — «тест» \"в кавычках\"" }),
            }, CancellationToken.None);

            Assert.False(result.GetProperty("isError").GetBoolean());
            var text = Text(result);
            Assert.Contains("Привет, мир — «тест» \"в кавычках\"!", text);
            Assert.DoesNotContain("\\u", result.GetRawText());
            Assert.DoesNotContain("\\u04", text);
        }
    }

    private static string Text(JsonElement toolResult)
        => toolResult.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;

    private static async Task<string> CallGetEnv(McpClientRegistry registry, string variableName)
    {
        return await CallGetEnvWith(registry, "echo-env-test", null, variableName);
    }

    private static async Task<string> CallGetEnvWith(
        McpClientRegistry registry,
        string serverId,
        Dictionary<string, string>? env,
        string variableName)
    {
        var result = await registry.CallToolAsync(new McpCallToolParams
        {
            ServerId = serverId,
            Command = GetEchoServerExePath(),
            ToolName = "get_env",
            Arguments = JsonSerializer.SerializeToElement(new { name = variableName }),
            Env = env,
        }, CancellationToken.None);

        return result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
    }
}
