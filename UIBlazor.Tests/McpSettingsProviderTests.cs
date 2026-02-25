using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Shared.Contracts;
using Shared.Contracts.Mcp;
using UIBlazor.Agents;
using UIBlazor.Options;
using UIBlazor.Services.Settings;
using UIBlazor.Tests.Utils;
using UIBlazor.Utils;
using UIBlazor.VS;

namespace UIBlazor.Tests;

public class McpSettingsProviderTests
{
    private readonly Mock<ILocalStorageService> _storageMock;
    private readonly Mock<IVsBridge> _vsBridgeMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly McpSettingsProvider _provider;
    private readonly ILogger<McpSettingsProvider> _logger;

    public McpSettingsProviderTests()
    {
        _storageMock = new Mock<ILocalStorageService>();
        _vsBridgeMock = new Mock<IVsBridge>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        _logger = new LoggerMock<McpSettingsProvider>();

        _provider = new McpSettingsProvider(_storageMock.Object, _logger, _vsBridgeMock.Object, _httpClient);
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
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<IReadOnlyDictionary<string, object>?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = "" });

        // Act
        await _provider.LoadAsync();

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
                { "test-server", new McpServerJsonEntry { Command = "node", Args = ["test.js"] } }
            }
        };

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, It.IsAny<IReadOnlyDictionary<string, object>?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = JsonUtils.Serialize(mcpJson) });

        // Mock RefreshToolsAsync for stdio
        var toolsResult = new McpResponse
        {
            Result = JsonDocument.Parse("{\"tools\": [{\"name\": \"tool1\", \"description\": \"desc1\", \"inputSchema\": {\"type\": \"object\", \"properties\": {}}}]}").RootElement
        };
        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.McpGetTools, It.IsAny<IReadOnlyDictionary<string, object>?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = JsonUtils.Serialize(toolsResult) });

        // Act
        await _provider.LoadAsync();

        // Assert
        Assert.Single(_provider.Current.Servers);
        var server = _provider.Current.Servers.First();
        Assert.Equal("test-server", server.Name);
        Assert.Equal("stdio", server.Transport);
        Assert.Single(server.Tools);
        Assert.Equal("tool1", server.Tools[0].Name);
    }

    [Fact]
    public async Task RefreshToolsAsync_HttpServer_HandlesSSEHandshakeAndToolsList()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "http-server",
            Transport = "http",
            Url = "http://localhost:8080/sse"
        };

        string? capturedId = null;
        var tcs = new TaskCompletionSource<string>();

        // 1. SSE Handshake response
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Get),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage r, CancellationToken c) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                // We use a custom stream to feed data dynamically
                var stream = new MemoryStream();
                var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                await writer.WriteLineAsync("data: /messages");
                await writer.WriteLineAsync("event: endpoint");
                await writer.WriteLineAsync("");
                
                // Keep the stream open until we provide the tools data
                _ = Task.Run(async () =>
                {
                    var id = await tcs.Task;
                    var toolsJson = JsonUtils.Serialize(new McpResponse
                    {
                        Id = id,
                        Result = JsonDocument.Parse("{\"tools\": [{\"name\": \"http-tool\", \"inputSchema\": {}}]}").RootElement
                    });
                    await writer.WriteLineAsync($"data: {toolsJson}");
                    await writer.WriteLineAsync("");
                    stream.Position = stream.Length; // Not quite right for SSE, but StreamReader might handle it
                });

                stream.Position = 0;
                response.Content = new StreamContent(stream);
                return response;
            });

        // 2. Tools list POST response - capture the ID
        _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage r, CancellationToken c) =>
            {
                var body = r.Content!.ReadAsStringAsync().Result;
                var request = JsonUtils.Deserialize<McpRequest>(body);
                capturedId = request!.Id;
                tcs.SetResult(capturedId!);
                return new HttpResponseMessage { StatusCode = HttpStatusCode.OK };
            });

        // Actually, the StreamReader in McpSettingsProvider will block on ReadLineAsync.
        // The MemoryStream approach might be tricky if it reaches EOF.
        // Let's use a simpler approach: Just provide a large enough buffer in the initial GET response
        // if we can predict the ID? No, we can't.
        
        // Let's try a different trick: Use a custom Stream that blocks until data is available.
    }
}
