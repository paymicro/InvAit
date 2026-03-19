using Shared.Contracts.Mcp;

namespace UIBlazor.Services.Settings;

public class McpSettingsProvider(
    ILocalStorageService storage,
    ILogger<McpSettingsProvider> logger,
    IVsBridge vsBridge,
    HttpClient httpClient)
    : BaseSettingsProvider<McpOptions>(storage, logger, "McpSettings"), IMcpSettingsProvider
{
    public async Task StopAllAsync()
    {
        await vsBridge.ExecuteToolAsync(BasicEnum.McpStopAll);
    }

    public override async Task ResetAsync()
    {
        Current.Enabled = true;
        Current.Servers = [];
        Current.ServerApprovalModes = [];
        Current.ServerErrors = [];
        Current.ServerEnabledStates = [];
        Current.ToolDisabledStates = [];
        await SaveAsync();
    }

    protected override async Task AfterInitAsync()
    {
        await LoadMcpFileAsync();
    }

    /// <summary>
    /// Load servers from %APPDATA%\Agent\mcp.json via VsBridge
    /// </summary>
    public async Task LoadMcpFileAsync()
    {
        try
        {
            logger.LogInformation("Loading MCP settings from mcp.json");
            Current.ServerErrors.Clear();
            var result = await vsBridge.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile);

#if DEBUG
            result = HeadlessMocker.GetVsToolResult(result);
#endif

            if (!result.Success || string.IsNullOrEmpty(result.Result))
            {
                logger.LogWarning(result.Success ? "mcp.json is empty" : $"Failed to read mcp.json: {result.ErrorMessage}");
                return;
            }

            var settingsFile = JsonUtils.Deserialize<McpSettingsFile>(result.Result);
            if (settingsFile?.McpServers == null)
            {
                logger.LogWarning("mcp.json has no servers defined");
                return;
            }

            var servers = new List<McpServerConfig>();
            var initServerTasks = new List<Task>();
            foreach (var (name, entry) in settingsFile.McpServers)
            {
                logger.LogInformation($"Loading server: {name}");
                var isRemote = !string.IsNullOrEmpty(entry.Url);
                var server = new McpServerConfig
                {
                    Name = name,
                    Transport = isRemote ? "http" : "stdio",
                    Command = entry.Command ?? string.Empty,
                    Args = entry.Args ?? [],
                    Url = entry.Url ?? string.Empty,
                    Endpoint = entry.Url ?? string.Empty,
                    Env = entry.Env ?? [],
                    Enabled = true
                };

                servers.Add(server);
                initServerTasks.Add(InitToolsAsync(server));
            }

            await Task.WhenAll(initServerTasks);
            Current.Servers = servers;
            logger.LogInformation($"MCP settings loaded: {servers.Count} servers");
            await SaveAsync();
        }
        catch (Exception ex)
        {
            logger.LogError($"Error loading MCP settings: {ex.Message}");
            Current.ServerErrors["__global__"] = ex.Message;
        }
    }

    private async Task InitToolsAsync(McpServerConfig server)
    {
        if (server.Tools.Count == 0)
        {
            var toolsResult = await RefreshToolsAsync(server);
            if (!toolsResult.StartsWith("Success"))
            {
                logger.LogError($"Failed to load tools for server {server.Name}: {toolsResult}");
                Current.ServerErrors[server.Name] = toolsResult;
                server.Enabled = false;
            }
            else
            {
                logger.LogInformation($"Loaded tools for server {server.Name}: {toolsResult}");
                Current.ServerErrors.Remove(server.Name);
            }
        }
    }

    /// <summary>
    /// Открыть mcp.json в редакторе VS
    /// </summary>
    public async Task OpenSettingsFileAsync()
    {
        await vsBridge.ExecuteToolAsync(BasicEnum.OpenMcpSettings);
    }

    public async Task<string> RefreshToolsAsync(McpServerConfig server)
    {
        var updateResult = $"Error refreshing tools for {server.Name}.";
        try
        {
            logger.LogInformation($"Refreshing tools for server: {server.Name} ({server.Transport})");

            if (server.Transport == "stdio")
            {
                var argsString = string.Join(" ", server.Args);
                var toolArgs = new Dictionary<string, object>
                {
                    { "serverId", server.Name },
                    { "command", server.Command },
                    { "args", argsString },
                    { "env", server.Env }
                };

                logger.LogInformation($"Starting stdio server: {server.Command} {argsString}");
                var result = await vsBridge.ExecuteToolAsync(BasicEnum.McpGetTools, toolArgs);
#if DEBUG
                result = HeadlessMocker.GetVsToolResult(result);
#endif
                if (!result.Success)
                {
                    logger.LogError($"Failed to get tools from {server.Name}: {result.ErrorMessage}");
                    return $"Error: {result.ErrorMessage}";
                }

                var mcpData = JsonUtils.Deserialize<JsonElement>(result.Result);
                updateResult = await UpdateServerToolsAsync(server, mcpData);
                logger.LogInformation($"Refresh result for {server.Name}: {updateResult}");
            }
            else // http sse
            {
                logger.LogInformation($"Connecting to HTTP MCP server: {server.Url}");
                // MCP SSE handshake
                using var request = new HttpRequestMessage(HttpMethod.Get, server.Url);
                using var handshakeResponse = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (!handshakeResponse.IsSuccessStatusCode)
                {
                    logger.LogError($"HTTP server {server.Name} returned status: {handshakeResponse.StatusCode}");
                    return $"Error: HTTP {handshakeResponse.StatusCode}";
                }
                var stream = await handshakeResponse.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                var postUrl = string.Empty;

                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;

                    if (line.StartsWith("data: ") && !line.Contains('{'))
                    {
                        var path = line[6..].Trim();
                        var baseUri = new Uri(server.Url);
                        postUrl = new Uri(baseUri, path).ToString();
                        logger.LogInformation($"HTTP MCP endpoint: {postUrl}");

                        var nextLine = await reader.ReadLineAsync();
                        if (nextLine != null && nextLine.StartsWith("event: endpoint"))
                        {
                            break;
                        }
                    }
                }

                var mcpRequest = new McpRequest
                {
                    Method = "tools/list",
                    Params = new { },
                    Id = "list_req_" + Guid.NewGuid().ToString("N")
                };

                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, postUrl)
                {
                    Content = new StringContent(JsonUtils.Serialize(mcpRequest), Encoding.UTF8, "application/json")
                };

                requestMessage.Content.Headers.ContentType!.CharSet = null;
                logger.LogInformation($"Requesting tools list from {postUrl}");
                var postResponse = await httpClient.SendAsync(requestMessage);

                if (!postResponse.IsSuccessStatusCode)
                {
                    logger.LogError($"HTTP server {server.Name} returned code: {postResponse.StatusCode}");
                    return $"{updateResult} {postResponse.StatusCode}";
                }

                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null)
                        break;

                    if (line.StartsWith("data: "))
                    {
                        var jsonData = line[6..].Trim();
                        var mcpData = JsonUtils.Deserialize<McpResponse>(jsonData);
                        if (mcpData?.Id == mcpRequest.Id && mcpData.Result is JsonElement jsonElement)
                        {
                            updateResult = await UpdateServerToolsAsync(server, jsonElement);
                            logger.LogInformation($"HTTP refresh result for {server.Name}: {updateResult}");
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError($"Error refreshing tools for {server.Name}: {ex.Message}");
            return $"Error: {ex.Message}";
        }

        // Restore tool enabled state from persisted settings
        if (server.Tools.Count > 0)
        {
            foreach (var tool in server.Tools)
            {
                var toolKey = $"{server.Name}:{tool.Name}";
                tool.Enabled = !Current.ToolDisabledStates.Contains(toolKey);
            }
        }

        return updateResult;
    }

    private async Task<string> UpdateServerToolsAsync(McpServerConfig server, JsonElement resultElement)
    {
        var listResult = resultElement.GetObject<McpListToolsResult>();
        if (listResult == null)
        {
            logger.LogError($"No tools found in response for {server.Name}");
            return $"Error: Not found tools";
        }

        var newTools = listResult.Tools.Select(t => new McpToolConfig
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = t.InputSchema as JsonElement?,
            RequiredArguments = ExtractRequiredArguments(t.InputSchema)
        }).ToList();

        logger.LogInformation($"Updating {server.Name} with {newTools.Count} tools");

        // Merge with existing tools (preserve enabled state)
        foreach (var tool in newTools)
        {
            var toolKey = $"{server.Name}:{tool.Name}";
            tool.Enabled = !Current.ToolDisabledStates.Contains(toolKey);
        }

        server.Tools = newTools;
        await SaveAsync();
        return $"Success: Found {newTools.Count} tools";
    }

    private static List<string> ExtractRequiredArguments(object? rawSchema)
    {
        var required = new List<string>();
        if (rawSchema is not JsonElement { ValueKind: JsonValueKind.Object } jsonElement ||
            !jsonElement.TryGetProperty("required", out var requiredProp) ||
            requiredProp.ValueKind != JsonValueKind.Array)
            return required;

        foreach (var item in requiredProp.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                required.Add(item.GetString()!);
            }
        }
        return required;
    }
}
