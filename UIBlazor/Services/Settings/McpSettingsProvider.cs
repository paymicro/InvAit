using Shared.Contracts.Mcp;

namespace UIBlazor.Services.Settings;

public class McpSettingsProvider(
    ILocalStorageService storage,
    ILogger<McpSettingsProvider> logger,
    IVsBridge vsBridge)
    : BaseSettingsProvider<McpOptions>(storage, logger, "McpSettings"), IMcpSettingsProvider
{
    public async Task StopAllAsync()
    {
        await vsBridge.ExecuteToolAsync(BasicEnum.McpStopAll, null);
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
        _ = LoadMcpFileAsync();
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
            var result = await vsBridge.ExecuteToolAsync(BasicEnum.ReadMcpSettingsFile, null);

#if DEBUG
            result = HeadlessMocker.GetVsToolResult(result);
#endif

            if (!result.Success || string.IsNullOrEmpty(result.Result))
            {
                logger.LogWarning(result.Success ? "mcp.json is empty" : $"Failed to read mcp.json: {result.ErrorMessage}");
                return;
            }

            var settingsFile = JsonUtils.Deserialize<McpSettingsFile>(result.Result);
            var fileServers = settingsFile?.GetServers();
            if (fileServers == null || fileServers.Count == 0)
            {
                logger.LogWarning("mcp.json has no servers defined");
                return;
            }

            foreach (var name in (settingsFile!.Mcp ?? []).Keys.Intersect((settingsFile.McpServers ?? []).Keys))
            {
                logger.LogWarning($"Server '{name}' defined in both 'mcp' and 'mcpServers'; using 'mcp' entry.");
            }

            var servers = new List<McpServerConfig>();
            var initServerTasks = new List<Task>();
            foreach (var (name, entry) in fileServers)
            {
                logger.LogInformation($"Loading server: {name} (type: {entry.Type ?? "local"})");

                var serverType = entry.Type?.Trim().ToLowerInvariant() ?? string.Empty;
                if (serverType is not ("" or "local" or "stdio" or "remote" or "http" or "streamable-http"))
                {
                    Current.ServerErrors[name] =
                        $"Unknown type '{entry.Type}'. Supported values: 'local', 'stdio', 'remote', 'http', 'streamable-http'.";
                    continue;
                }

                var isRemote = serverType is "remote" or "http" or "streamable-http"
                    || (serverType == "" && !string.IsNullOrEmpty(entry.Url));
                var enabled = entry.Enabled ?? true;

                McpServerConfig server;
                if (isRemote)
                {
                    if (string.IsNullOrEmpty(entry.Url))
                    {
                        Current.ServerErrors[name] = "'type': 'remote' requires a 'url' field.";
                        continue;
                    }

                    if (entry.Oauth == true)
                    {
                        Current.ServerErrors[name] = "OAuth MCP servers are not supported yet. Use static headers instead.";
                        continue;
                    }

                    server = new McpServerConfig
                    {
                        Name = name,
                        Transport = "http",
                        Url = entry.Url!,
                        Endpoint = entry.Url!,
                        Headers = entry.Headers ?? [],
                        Env = entry.EffectiveEnv ?? [],
                        Enabled = enabled
                    };
                }
                else
                {
                    if (string.IsNullOrEmpty(entry.CommandProgram))
                    {
                        Current.ServerErrors[name] =
                            "Local MCP server requires a 'command' field, e.g. \"command\": [\"npx\", \"-y\", \"@modelcontextprotocol/server-everything\"].";
                        continue;
                    }

                    if (!string.IsNullOrEmpty(entry.Url))
                    {
                        logger.LogInformation($"Server '{name}' is local; ignoring its 'url' field.");
                    }

                    server = new McpServerConfig
                    {
                        Name = name,
                        Transport = "stdio",
                        Command = entry.CommandProgram!,
                        Args = entry.EffectiveArgs.ToArray(),
                        Headers = entry.Headers ?? [],
                        Env = entry.EffectiveEnv ?? [],
                        Enabled = enabled
                    };
                }

                servers.Add(server);

                if (enabled)
                {
                    initServerTasks.Add(InitToolsAsync(server));
                }
            }

            await Task.WhenAll(initServerTasks);
            PruneOrphanedStates(servers);
            Current.Servers = servers;
            logger.LogInformation($"MCP settings loaded: {servers.Count} servers");
            await SaveAsync();
        }
        catch (JsonException jex)
        {
            var position = jex.LineNumber >= 0 ? $" (line {jex.LineNumber + 1})" : string.Empty;
            logger.LogError($"mcp.json parse error{position}: {jex.Message}");
            Current.ServerErrors["__global__"] = $"mcp.json parse error{position}: {jex.Message}. Fix the file and press reload.";
        }
        catch (Exception ex)
        {
            logger.LogError($"Error loading MCP settings: {ex.Message}");
            Current.ServerErrors["__global__"] = ex.Message;
        }
    }

    private void PruneOrphanedStates(List<McpServerConfig> servers)
    {
        var names = servers.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in Current.ServerEnabledStates.Keys.Where(k => !names.Contains(k)).ToList())
        {
            Current.ServerEnabledStates.Remove(stale);
        }

        foreach (var stale in Current.ServerApprovalModes.Keys.Where(k => !names.Contains(k)).ToList())
        {
            Current.ServerApprovalModes.Remove(stale);
        }

        foreach (var stale in Current.ToolDisabledStates
                     .Where(k => !names.Contains(k.Split(':')[0]))
                     .ToList())
        {
            Current.ToolDisabledStates.Remove(stale);
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
        await vsBridge.ExecuteToolAsync(BasicEnum.OpenMcpSettings, null);
    }

    public async Task<string> RefreshToolsAsync(McpServerConfig server)
    {
        var updateResult = $"Error refreshing tools for {server.Name}.";
        try
        {
            logger.LogInformation($"Refreshing tools for server: {server.Name} ({server.Transport})");

            var toolArgs = new Dictionary<string, object>
            {
                { "serverId", server.Name },
                { "command", server.Command },
                { "args", server.Args },
                { "env", server.Env },
                { "url", server.Url },
                { "headers", server.Headers }
            };

            if (server.Transport == "stdio")
            {
                logger.LogInformation($"Starting stdio server: {server.Command} {string.Join(" ", server.Args)}");
            }
            else
            {
                logger.LogInformation($"Connecting to HTTP MCP server: {server.Url}");
            }

            var result = await vsBridge.ExecuteToolAsync(BasicEnum.McpGetTools, JsonUtils.Serialize(toolArgs));
#if DEBUG
            result = HeadlessMocker.GetVsToolResult(result);
#endif
            if (!result.Success)
            {
                logger.LogError($"Failed to get tools from {server.Name}: {result.ErrorMessage}");
                return $"Error: {result.ErrorMessage}";
            }

            var mcpData = JsonUtils.Deserialize<JsonElement>(result.Result);
            if (mcpData.ValueKind != JsonValueKind.Object)
            {
                var snippet = result.Result is { Length: > 0 } raw ? raw[..Math.Min(raw.Length, 800)] : "<empty payload>";
                logger.LogError($"Unexpected tools payload from {server.Name} ({mcpData.ValueKind}): {snippet}");
                return $"Error: Unexpected tools payload (see output window).";
            }

            updateResult = await UpdateServerToolsAsync(server, mcpData);
            logger.LogInformation($"Refresh result for {server.Name}: {updateResult}");
        }
        catch (Exception ex)
        {
            logger.LogError($"Error refreshing tools for {server.Name}: {ex.Message}");
            return $"Error: {ex.Message}";
        }

        return updateResult;
    }

    private async Task<string> UpdateServerToolsAsync(McpServerConfig server, JsonElement resultElement)
    {
        var listResult = resultElement.GetObject<McpListToolsResult>();
        if (listResult == null)
        {
            logger.LogError($"No tools found in response for {server.Name}");
            return "Error: Not found tools";
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
