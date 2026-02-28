using System.Diagnostics;
using Shared.Contracts.Mcp;

namespace UIBlazor.Services.Settings;

public class McpSettingsProvider(
    ILocalStorageService storage,
    ILogger<McpSettingsProvider> logger,
    IVsBridge vsBridge,
    HttpClient httpClient)
    : BaseSettingsProvider<McpOptions>(storage, logger, "McpSettings"), IMcpSettingsProvider
{
    private static void Log(string message, string level = "INFO") =>
        Debug.WriteLine($"[MCP {level}] {message}");

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
            Log("Loading MCP settings from mcp.json");
            Current.ServerErrors.Clear();
            var result = await vsBridge.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile);

#if DEBUG
            result = HeadlessMocker.GetVsToolResult(result);
#endif

            if (!result.Success || string.IsNullOrEmpty(result.Result))
            {
                Log(result.Success ? "mcp.json is empty" : $"Failed to read mcp.json: {result.ErrorMessage}", "WARN");
                return;
            }

            var settingsFile = JsonUtils.Deserialize<McpSettingsFile>(result.Result);
            if (settingsFile?.McpServers == null)
            {
                Log("mcp.json has no servers defined", "WARN");
                return;
            }

            var servers = new List<McpServerConfig>();
            foreach (var (name, entry) in settingsFile.McpServers)
            {
                Log($"Loading server: {name}");
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

                if (server.Tools.Count == 0)
                {
                    var toolsResult = await RefreshToolsAsync(server);
                    if (!toolsResult.StartsWith("Success"))
                    {
                        Log($"Failed to load tools for server {name}: {toolsResult}", "ERROR");
                        Current.ServerErrors[name] = toolsResult;
                        server.Enabled = false;
                    }
                    else
                    {
                        Log($"Loaded tools for server {name}: {toolsResult}");
                        Current.ServerErrors.Remove(name);
                    }
                }

                // Restore tool enabled state from persisted settings
                if (server.Tools.Count > 0)
                {
                    foreach (var tool in server.Tools)
                    {
                        var toolKey = $"{name}:{tool.Name}";
                        tool.Enabled = !Current.ToolDisabledStates.Contains(toolKey);
                    }
                }

                servers.Add(server);
            }

            Current.Servers = servers;
            Log($"MCP settings loaded: {servers.Count} servers");
            await SaveAsync();
        }
        catch (Exception ex)
        {
            Log($"Error loading MCP settings: {ex.Message}", "ERROR");
            Current.ServerErrors["__global__"] = ex.Message;
        }
    }

    /// <summary>
    /// Open mcp.json
    /// </summary>
    public async Task OpenSettingsFileAsync()
    {
        await vsBridge.ExecuteToolAsync(BasicEnum.OpenMcpSettings);
    }

    public async Task<string> RefreshToolsAsync(McpServerConfig server)
    {
        try
        {
            Log($"Refreshing tools for server: {server.Name} ({server.Transport})");

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

                Log($"Starting stdio server: {server.Command} {argsString}");
                var result = await vsBridge.ExecuteToolAsync(BasicEnum.McpGetTools, toolArgs);
#if DEBUG
                result = HeadlessMocker.GetVsToolResult(result);
#endif
                if (!result.Success)
                {
                    Log($"Failed to get tools from {server.Name}: {result.ErrorMessage}", "ERROR");
                    return $"Error: {result.ErrorMessage}";
                }

                var mcpData = JsonUtils.Deserialize<McpResponse>(result.Result);
                if (mcpData?.Result is JsonElement jsonElement)
                {
                    var updateResult = await UpdateServerTools(server, jsonElement);
                    Log($"Refresh result for {server.Name}: {updateResult}");
                    return updateResult;
                }

                Log($"Could not parse tools list from {server.Name}", "ERROR");
                return "Error: Could not parse tools list";
            }
            else // http
            {
                Log($"Connecting to HTTP MCP server: {server.Url}");
                // MCP SSE handshake
                using var request = new HttpRequestMessage(HttpMethod.Get, server.Url);
                using var handshakeResponse = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (!handshakeResponse.IsSuccessStatusCode)
                {
                    Log($"HTTP server {server.Name} returned status: {handshakeResponse.StatusCode}", "ERROR");
                    return $"Error: HTTP {handshakeResponse.StatusCode}";
                }
                var stream = await handshakeResponse.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                var postUrl = string.Empty;

                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;

                    if (line.StartsWith("data: ") && !line.Contains("{"))
                    {
                        var path = line.Substring(6).Trim();
                        var baseUri = new Uri(server.Url);
                        postUrl = new Uri(baseUri, path).ToString();
                        Log($"HTTP MCP endpoint: {postUrl}");

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
                Log($"Requesting tools list from {postUrl}");
                var postResponse = await httpClient.SendAsync(requestMessage);

                if (postResponse.IsSuccessStatusCode)
                {
                    while (true)
                    {
                        var line = await reader.ReadLineAsync();
                        if (line == null) break;

                        if (line.StartsWith("data: "))
                        {
                            var jsonData = line.Substring(6).Trim();
                            var mcpData = JsonUtils.Deserialize<McpResponse>(jsonData);
                            if (mcpData?.Id == mcpRequest.Id && mcpData.Result is JsonElement jsonElement)
                            {
                                var updateResult = await UpdateServerTools(server, jsonElement);
                                Log($"HTTP refresh result for {server.Name}: {updateResult}");
                                return updateResult;
                            }
                        }
                    }
                }

                Log($"Could not parse tools list from HTTP server {server.Name}", "ERROR");
                return "Error: Could not parse tools list";
            }
        }
        catch (Exception ex)
        {
            Log($"Error refreshing tools for {server.Name}: {ex.Message}", "ERROR");
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> UpdateServerTools(McpServerConfig server, JsonElement resultElement)
    {
        var listResult = resultElement.GetObject<McpListToolsResult>();
        if (listResult == null)
        {
            Log($"No tools found in response for {server.Name}", "ERROR");
            return $"Error: Not found tools";
        }

        var newTools = listResult.Tools.Select(t => new McpToolConfig
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = t.InputSchema as JsonElement?,
            RequiredArguments = ExtractRequiredArguments(t.InputSchema)
        }).ToList();

        Log($"Updating {server.Name} with {newTools.Count} tools");

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
        if (rawSchema is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Object)
        {
            if (jsonElement.TryGetProperty("required", out var requiredProp) && requiredProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in requiredProp.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        required.Add(item.GetString()!);
                    }
                }
            }
        }
        return required;
    }
}
