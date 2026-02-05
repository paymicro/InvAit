using Shared.Contracts.Mcp;

namespace UIBlazor.Services.Settings;

public class McpSettingsProvider(ILocalStorageService storage, IVsBridge vsBridge, HttpClient httpClient)
    : BaseSettingsProvider<McpOptions>(storage, "McpSettings"), IMcpSettingsProvider
{
    public override async Task ResetAsync()
    {
        Current.Enabled = true;
        Current.Servers = new();
        await SaveAsync();
    }

    public async Task<string> RefreshToolsAsync(string serverId)
    {
        var server = Current.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null)
        {
            return "Error: server not found";
        }

        try
        {
            if (server.Transport == "stdio")
            {
                var result = await vsBridge.ExecuteToolAsync(BuiltInToolEnum.McpGetTools, new Dictionary<string, object>
                {
                    { "serverId", server.Id },
                    { "command", server.Command },
                    { "args", server.Args }
                });

                if (!result.Success)
                {
                    return $"Error: {result.ErrorMessage}";
                }

                var mcpData = JsonUtils.Deserialize<McpResponse>(result.Result);
                if (mcpData?.Result is JsonElement jsonElement)
                {
                    return await UpdateServerTools(server, jsonElement);
                }
                
                return "Error: Could not parse tools list";
            }
            else // http
            {
                // MCP SSE handshake
                // 1. Открываем SSE поток (GET)
                using var request = new HttpRequestMessage(HttpMethod.Get, server.Endpoint);
                using var handshakeResponse = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (!handshakeResponse.IsSuccessStatusCode)
                {
                    return "Error";
                }
                var stream = await handshakeResponse.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);

                var postUrl = string.Empty;

                // 2. Читаем поток, пока не получим URL для POST
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;

                    if (line.StartsWith("data: ") && !line.Contains("{")) // Ищем строку с путем
                    {
                        var path = line.Substring(6).Trim(); // Получим "/message?sessionId=..."
                        var baseUri = new Uri(server.Endpoint);
                        postUrl = new Uri(baseUri, path).ToString(); // Склеиваем в полный URL

                        // Проверяем следующее событие, чтобы убедиться, что это endpoint
                        var nextLine = await reader.ReadLineAsync();
                        if (nextLine != null && nextLine.StartsWith("event: endpoint"))
                        {
                            break; // URL найден, выходим из цикла поиска
                        }
                    }
                }

                // 3. ОТПРАВЛЯЕМ ЗАПРОС (Сервер ответит 202/Accepted)
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

                // Force remove the charset from Content-Type header as some MCP servers reject it
                requestMessage.Content.Headers.ContentType!.CharSet = null;
                var postResponse = await httpClient.SendAsync(requestMessage);

                if (postResponse.IsSuccessStatusCode)
                {
                    // 4. ТЕПЕРЬ САМОЕ ВАЖНОЕ: Читаем ТОТ ЖЕ reader дальше!
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
                                return await UpdateServerTools(server, jsonElement);
                            }
                        }
                    }
                }

                return "Error: Could not parse tools list";
            }
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> UpdateServerTools(McpServerConfig server, JsonElement resultElement)
    {
        var listResult = resultElement.GetObject<McpListToolsResult>();
        if (listResult == null)
        {
            return $"Error: Not found tools";
        }

        var newTools = listResult.Tools.Select(t => new McpToolConfig
        {
            Name = t.Name,
            Description = t.Description,
            Enabled = true
        }).ToList();

        // Merge with existing tools
        foreach (var tool in newTools)
        {
            var existing = server.Tools.FirstOrDefault(et => et.Name == tool.Name);
            if (existing != null)
            {
                tool.Enabled = existing.Enabled;
            }
        }

        server.Tools = newTools;
        await SaveAsync();
        return $"Success: Found {newTools.Count} tools";
    }
}
