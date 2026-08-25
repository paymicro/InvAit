namespace UIBlazor.Tests.Services.Settings;

public class McpSettingsProviderTests
{
    private readonly Mock<ILocalStorageService> _storageMock;
    private readonly Mock<IVsBridge> _vsBridgeMock;
    private readonly McpSettingsProvider _provider;
    private readonly ILogger<McpSettingsProvider> _logger;

    public McpSettingsProviderTests()
    {
        _storageMock = new Mock<ILocalStorageService>();
        _vsBridgeMock = new Mock<IVsBridge>();
        _logger = new LoggerMock<McpSettingsProvider>();

        _provider = new McpSettingsProvider(_storageMock.Object, _logger, _vsBridgeMock.Object);
    }

    [Fact]
    public async Task ResetAsync_ResetsToDefaultState()
    {
        // Act
        await _provider.ResetAsync();

        // Assert
        Assert.True(_provider.Current.Enabled);
        Assert.Empty(_provider.Current.Servers);
        _storageMock.Verify(s => s.SetItemAsync("McpSettings", It.IsAny<McpOptions>()), Times.Once);
    }

    [Fact]
    public async Task LoadAsync_HandlesEmptyMcpJson()
    {
        // Arrange
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = "" });

        // Act
        await _provider.LoadMcpFileAsync();

        // Assert
        Assert.Empty(_provider.Current.Servers);
    }

    [Fact]
    public async Task LoadAsync_ParsesStdioServer()
    {
        // Arrange
        var mcpJson = new McpSettingsFile
        {
            McpServers = new Dictionary<string, McpServerJsonEntry>
            {
                { "test-server", new McpServerJsonEntry { Command = ["node", "test.js"] } }
            }
        };

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = JsonUtils.Serialize(mcpJson) });

        // Mock RefreshToolsAsync for stdio
        var toolsResult = new
        {
            tools = new[]
            {
                new {
                    name = "tool1",
                    description = "desc1",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new { }
                    }
                }
            }
        };
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.McpGetTools, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = JsonUtils.Serialize(toolsResult) });

        // Act
        await _provider.LoadMcpFileAsync();

        // Assert
        Assert.Single(_provider.Current.Servers);
        var server = _provider.Current.Servers.First();
        Assert.Equal("test-server", server.Name);
        Assert.Equal("stdio", server.Transport);
        Assert.Single(server.Tools);
        Assert.Equal("tool1", server.Tools[0].Name);
    }

    [Fact]
    public async Task LoadAsync_BothRoots_LoadsServersFromBoth()
    {
        const string mcpJson = """
        {
          "mcp": {
            "opencode-server": { "command": ["node", "oc.js"] }
          },
          "mcpServers": {
            "claude-server": { "command": "node claude.js" }
          }
        }
        """;

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = mcpJson });
        SetupSuccessfulTools();

        await _provider.LoadMcpFileAsync();

        Assert.Equal(2, _provider.Current.Servers.Count);
        Assert.Contains(_provider.Current.Servers, s => s.Name == "opencode-server" && s.Transport == "stdio");
        Assert.Contains(_provider.Current.Servers, s => s.Name == "claude-server" && s.Transport == "stdio");
        Assert.True(_provider.Current.ServerErrors.Count == 0);
    }

    [Fact]
    public async Task LoadAsync_NameConflict_McpEntryWins()
    {
        const string mcpJson = """
        {
          "mcp": { "dup": { "command": ["node", "from-mcp.js"] } },
          "mcpServers": { "dup": { "command": ["deno", "from-legacy.js"] } }
        }
        """;

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = mcpJson });
        SetupSuccessfulTools();

        await _provider.LoadMcpFileAsync();

        Assert.Single(_provider.Current.Servers);
        Assert.Equal("node", _provider.Current.Servers[0].Command);
        Assert.Equal(["from-mcp.js"], _provider.Current.Servers[0].Args);
    }

    [Fact]
    public async Task LoadAsync_StdioTypeAlias_LoadsAsLocalServer()
    {
        var mcpJson = """{"mcp":{"stdio-alias":{"type":"stdio","command":["node","srv.js"]}}}""";

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = mcpJson });
        SetupSuccessfulTools();

        await _provider.LoadMcpFileAsync();

        var server = _provider.Current.Servers.Single();
        Assert.Equal("stdio", server.Transport);
        Assert.Equal("node", server.Command);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("streamable-http")]
    [InlineData("HTTP")]
    [InlineData(" Streamable-Http ")]
    public async Task LoadAsync_RemoteTypeAliases_LoadAsHttpServers(string type)
    {
        var mcpJson = "{\"mcp\":{\"remote-alias\":{\"type\":\"" + type + "\",\"url\":\"https://x/mcp\"}}}";

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = mcpJson });
        SetupSuccessfulTools();

        await _provider.LoadMcpFileAsync();

        var server = _provider.Current.Servers.Single();
        Assert.Equal("http", server.Transport);
        Assert.Equal("https://x/mcp", server.Url);
    }

    [Fact]
    public async Task LoadAsync_CorruptedJson_SetsGlobalParseError()
    {
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = "{ not json" });

        await _provider.LoadMcpFileAsync();

        Assert.True(_provider.Current.ServerErrors.ContainsKey("__global__"));
        Assert.Contains("parse error", _provider.Current.ServerErrors["__global__"]);
    }

    [Fact]
    public async Task LoadAsync_PrunesOrphanedStates_KeepsLiveOnes()
    {
        _provider.Current.ToolDisabledStates.Add("ghost:t");
        _provider.Current.ServerApprovalModes["ghost"] = ToolApprovalMode.Ask;
        _provider.Current.ServerEnabledStates["live"] = true;

        const string mcpJson = """{"mcp":{"live":{"command":["node","srv.js"]}}}""";
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = mcpJson });
        SetupSuccessfulTools();

        await _provider.LoadMcpFileAsync();

        Assert.DoesNotContain("ghost:t", _provider.Current.ToolDisabledStates);
        Assert.False(_provider.Current.ServerApprovalModes.ContainsKey("ghost"));
        Assert.True(_provider.Current.ServerEnabledStates.ContainsKey("live"));
        Assert.Single(_provider.Current.Servers);
        Assert.Equal("live", _provider.Current.Servers[0].Name);
    }

    [Fact]
    public async Task LoadAsync_EmptyMcpRoot_LegacyRootStillLoads()
    {
        const string mcpJson = """{"mcp":{},"mcpServers":{"legacy":{"command":["node","legacy.js"]}}}""";

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = mcpJson });
        SetupSuccessfulTools();

        await _provider.LoadMcpFileAsync();

        var server = Assert.Single(_provider.Current.Servers);
        Assert.Equal("legacy", server.Name);
        Assert.Equal("node", server.Command);
        Assert.Equal(["legacy.js"], server.Args);
    }

    private void SetupSuccessfulTools()
    {
        var toolsResult = new
        {
            tools = new[]
            {
                new
                {
                    name = "tool1",
                    description = "desc1",
                    inputSchema = new { type = "object", properties = new { } }
                }
            }
        };
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.McpGetTools, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = JsonUtils.Serialize(toolsResult) });
    }

    [Fact]
    public async Task RefreshToolsAsync_HttpServer_RoutesThroughVsBridgeWithUrl()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "http-server",
            Transport = "http",
            Url = "http://localhost:8080/mcp"
        };

        string? capturedPayload = null;
        var toolsResult = new
        {
            tools = new[]
            {
                new {
                    name = "http-tool",
                    description = "desc",
                    inputSchema = new { type = "object", properties = new { } }
                }
            }
        };
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.McpGetTools, It.IsAny<string?>()))
            .Callback<string, string?, CancellationToken>((_, payload, _) => capturedPayload = payload)
            .ReturnsAsync(new VsToolResult { Success = true, Result = JsonUtils.Serialize(toolsResult) });

        // Act
        var updateResult = await _provider.RefreshToolsAsync(server);

        // Assert
        Assert.Contains("Success", updateResult);
        Assert.NotNull(capturedPayload);
        Assert.Contains("http-server", capturedPayload);
        Assert.Contains("http://localhost:8080/mcp", capturedPayload);
        Assert.Single(server.Tools);
        Assert.Equal("http-tool", server.Tools[0].Name);
    }

    [Fact]
    public async Task LoadAsync_OpenCodeRemoteWithoutUrl_RecordsError()
    {
        var mcpJson = """{"mcp":{"remote-broken":{"type":"remote"}}}""";

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = mcpJson });

        await _provider.LoadMcpFileAsync();

        Assert.Empty(_provider.Current.Servers);
        Assert.True(_provider.Current.ServerErrors.ContainsKey("remote-broken"));
        Assert.Contains("url", _provider.Current.ServerErrors["remote-broken"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_LocalWithoutCommand_RecordsError()
    {
        var mcpJson = """{"mcp":{"local-broken":{"type":"local"}}}""";

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = mcpJson });

        await _provider.LoadMcpFileAsync();

        Assert.Empty(_provider.Current.Servers);
        Assert.True(_provider.Current.ServerErrors.ContainsKey("local-broken"));
        Assert.Contains("command", _provider.Current.ServerErrors["local-broken"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_RemoteWithHeaders_PassesHeadersToHostPayload()
    {
        var mcpJson = """
        {
          "mcp": {
            "gh": {
              "type": "remote",
              "url": "https://api.githubcopilot.com/mcp/",
              "headers": { "Authorization": "Bearer PAT" }
            }
          }
        }
        """;

        string? capturedPayload = null;
        var toolsResult = new
        {
            tools = new[]
            {
                new { name = "r-tool", description = "d", inputSchema = new { type = "object", properties = new { } } }
            }
        };
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = mcpJson });
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.McpGetTools, It.IsAny<string?>()))
            .Callback<string, string?, CancellationToken>((_, payload, _) => capturedPayload = payload)
            .ReturnsAsync(new VsToolResult { Success = true, Result = JsonUtils.Serialize(toolsResult) });

        await _provider.LoadMcpFileAsync();

        var server = _provider.Current.Servers.Single();
        Assert.Equal("http", server.Transport);
        Assert.Equal("Bearer PAT", server.Headers["Authorization"]);
        Assert.NotNull(capturedPayload);
        Assert.Contains("Bearer PAT", capturedPayload);
        Assert.Contains("\"Authorization\"", capturedPayload);
    }

    [Fact]
    public async Task LoadAsync_OauthRemote_RecordsUnsupportedError()
    {
        var mcpJson = """{"mcp":{"oauth-srv":{"type":"remote","url":"https://x/mcp","oauth":true}}}""";

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = mcpJson });

        await _provider.LoadMcpFileAsync();

        Assert.Empty(_provider.Current.Servers);
        Assert.Contains("OAuth", _provider.Current.ServerErrors["oauth-srv"]);
    }

    [Fact]
    public async Task LoadAsync_EnabledFalse_SkipsToolInitialization()
    {
        var mcpJson = """{"mcp":{"disabled":{"command":"node","enabled":false}}}""";

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = mcpJson });

        await _provider.LoadMcpFileAsync();

        var server = _provider.Current.Servers.Single();
        Assert.False(server.Enabled);
        Assert.Empty(server.Tools);
        _vsBridgeMock.Verify(
            v => v.ExecuteToolAsync(BasicEnum.McpGetTools, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LoadAsync_UnknownType_RecordsError()
    {
        var mcpJson = """{"mcp":{"weird":{"type":"docker","command":"x"}}}""";

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<string?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = mcpJson });

        await _provider.LoadMcpFileAsync();

        Assert.Empty(_provider.Current.Servers);
        Assert.Contains("docker", _provider.Current.ServerErrors["weird"]);
    }
}
