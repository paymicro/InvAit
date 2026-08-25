using System.Text.Json;
using McpHost;
using Shared.Contracts.Mcp;
using Shared.Contracts.McpHost;

namespace ToolCore.Tests;

public class McpConfigParsingTests
{
    private static McpSettingsFile? Parse(string json)
        => JsonSerializer.Deserialize<McpSettingsFile>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        });

    [Theory]
    [InlineData("{\"mcp\":{\"a\":{\"command\":\"node\"}}}", "node")]
    [InlineData("{\"mcp\":{\"a\":{\"type\":\"local\",\"command\":[\"npx\",\"-y\",\"pkg\"]}}}", "npx")]
    public void Command_AcceptsStringAndArray(string json, string expectedProgram)
    {
        var file = Parse(json);
        var entry = file!.GetServers()["a"];

        Assert.Equal(expectedProgram, entry.CommandProgram);
    }

    [Fact]
    public void Command_ArrayTail_MergesWithArgs()
    {
        var file = Parse("""{"mcp":{"a":{"command":["npx","-y","pkg"],"args":["--x"]}}}""");
        var entry = file!.GetServers()["a"];

        Assert.Equal(["-y", "pkg", "--x"], entry.EffectiveArgs.ToArray());
    }

    [Fact]
    public void MissingType_MeansLocal_EnabledDefaultsTrue()
    {
        var file = Parse("""{"mcp":{"a":{"command":"node"}}}""");
        var entry = file!.GetServers()["a"];

        Assert.Null(entry.Type);
        Assert.True(entry.Enabled ?? true);
    }

    [Fact]
    public void LegacyMcpServersRoot_StillParses()
    {
        var file = Parse("""{"mcpServers":{"old":{"command":"node"}}}""");

        Assert.Empty(file!.Mcp ?? []);
        Assert.True(file.GetServers().ContainsKey("old"));
    }

    [Fact]
    public void BothRoots_DistinctNames_MergeIntoTwoServers()
    {
        var file = Parse("""
        {"mcp":{"new":{"command":"a"}},"mcpServers":{"legacy":{"command":"b"}}}
        """);

        var servers = file!.GetServers();

        Assert.Equal(2, servers.Count);
        Assert.Equal("a", servers["new"].CommandProgram);
        Assert.Equal("b", servers["legacy"].CommandProgram);
    }

    [Fact]
    public void NameConflict_McpRootWins()
    {
        var file = Parse("""
        {"mcp":{"dup":{"command":"from-mcp"}},"mcpServers":{"dup":{"command":"from-legacy"},"other":{"command":"c"}}}
        """);

        var servers = file!.GetServers();

        Assert.Equal(2, servers.Count);
        Assert.Equal("from-mcp", servers["dup"].CommandProgram);
        Assert.Equal("c", servers["other"].CommandProgram);
    }

    [Theory]
    [InlineData("stdio")]
    [InlineData("http")]
    [InlineData("streamable-http")]
    public void TypeAliases_ParseWithoutRejection(string type)
    {
        var file = Parse("{\"mcp\":{\"a\":{\"type\":\"" + type + "\"}}}");

        Assert.Equal(type, file!.GetServers()["a"].Type);
    }

    [Fact]
    public void Environment_FallsBackToEnv()
    {
        var file = Parse("""{"mcp":{"a":{"command":"node","environment":{"K1":"v1"}}}}""");
        Assert.Equal("v1", file!.GetServers()["a"].EffectiveEnv!["K1"]);

        file = Parse("""{"mcp":{"b":{"command":"node","env":{"K2":"v2"}}}}""");
        Assert.Equal("v2", file!.GetServers()["b"].EffectiveEnv!["K2"]);
    }

    [Fact]
    public void Remote_Entry_ParsesUrlHeadersOauth()
    {
        var file = Parse("""
        {
          "mcp": {
            "gh": {
              "type": "remote",
              "url": "https://api.githubcopilot.com/mcp/",
              "oauth": false,
              "headers": { "Authorization": "Bearer PAT" }
            }
          }
        }
        """);
        var entry = file!.GetServers()["gh"];

        Assert.Equal("remote", entry.Type);
        Assert.Equal("https://api.githubcopilot.com/mcp/", entry.Url);
        Assert.False(entry.Oauth ?? false);
        Assert.Equal("Bearer PAT", entry.Headers!["Authorization"]);
    }

    [Fact]
    public void InvalidCommandToken_ThrowsJsonException()
    {
        Assert.ThrowsAny<JsonException>(
            () => Parse("""{"mcp":{"a":{"command":[42]}}}"""));
    }

    [Fact]
    public void Converter_Write_SingleElement_WritesBareString()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new StringOrStringArrayConverter());

        Assert.Equal("\"node\"", JsonSerializer.Serialize(new[] { "node" }, options));
    }

    [Fact]
    public void Converter_Write_MultipleElements_WritesArray()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new StringOrStringArrayConverter());

        var json = JsonSerializer.Serialize(new[] { "npx", "-y", "pkg" }, options);

        Assert.Equal("""["npx","-y","pkg"]""", json);
    }

    [Fact]
    public void Converter_Write_Null_WritesNull()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new StringOrStringArrayConverter());

        Assert.Equal("null", JsonSerializer.Serialize((string[]?)null, options));
    }

    [Fact]
    public void Converter_Read_BareString_RoundTripsToArray()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new StringOrStringArrayConverter());

        var value = JsonSerializer.Deserialize<string[]?>("\"node\"", options);

        Assert.Equal(["node"], value);
    }

    [Fact]
    public void Fingerprint_DistinguishesArgsOrder()
    {
        var first = McpClientRegistry.BuildFingerprint(
            new McpListToolsParams { ServerId = "s", Command = "node", Args = ["--x", "--y"] });
        var sameAsFirst = McpClientRegistry.BuildFingerprint(
            new McpListToolsParams { ServerId = "s", Command = "node", Args = ["--x", "--y"] });
        var reordered = McpClientRegistry.BuildFingerprint(
            new McpListToolsParams { ServerId = "s", Command = "node", Args = ["--y", "--x"] });

        Assert.Equal(first, sameAsFirst);
        Assert.NotEqual(first, reordered);
    }

    [Fact]
    public void Fingerprint_DistinguishesHeaders()
    {
        var baseParams = new McpListToolsParams
        {
            ServerId = "s",
            Url = "https://x/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer A" },
        };

        var same = McpClientRegistry.BuildFingerprint(baseParams);
        var stable = McpClientRegistry.BuildFingerprint(baseParams);
        var withOtherHeader = McpClientRegistry.BuildFingerprint(new McpListToolsParams
        {
            ServerId = "s",
            Url = "https://x/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer B" },
        });
        var withExtraHeader = McpClientRegistry.BuildFingerprint(new McpListToolsParams
        {
            ServerId = "s",
            Url = "https://x/mcp",
            Headers = new Dictionary<string, string> { ["X-Other"] = "1", ["Authorization"] = "Bearer A" },
        });
        var headerCaseInsensitiveKey = McpClientRegistry.BuildFingerprint(new McpListToolsParams
        {
            ServerId = "s",
            Url = "https://x/mcp",
            Headers = new Dictionary<string, string> { ["AUTHORIZATION"] = "Bearer A" },
        });

        Assert.Equal(same, stable);
        Assert.NotEqual(same, withOtherHeader);
        Assert.NotEqual(same, withExtraHeader);
        Assert.Equal(same, headerCaseInsensitiveKey);

        var stdioParams = new McpListToolsParams { ServerId = "s", Command = "node", Headers = baseParams.Headers };
        var stdioNoHeaders = McpClientRegistry.BuildFingerprint(new McpListToolsParams { ServerId = "s", Command = "node" });
        Assert.Equal(stdioNoHeaders, McpClientRegistry.BuildFingerprint(stdioParams));
    }
}
