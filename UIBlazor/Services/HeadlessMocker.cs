using Shared.Contracts.Mcp;

namespace UIBlazor.Services;

/// <summary>
/// Для тестов на голом UI без VS
/// </summary>
public class HeadlessMocker
{
    public static VsToolResult GetVsToolResult(VsToolResult original)
    {
        if (original.Success || original.ErrorMessage != "WebView2 API is`t find.")
        {
            return original;
        }

        return original.Name switch
        {
            BasicEnum.ReadMcpSettingsFile => new VsToolResult
            {
                Result = JsonUtils.Serialize(new McpSettingsFile
                {
                    McpServers = new Dictionary<string, McpServerJsonEntry>
                    {
                        {
                            "ui-test",
                            new McpServerJsonEntry
                            {
                                Command = "npx",
                                Args = ["-y", "ui-test"],
                                Env = new Dictionary<string, string> { { "env1", "val1" } }
                            }
                        }
                    }
                })
            },
            BasicEnum.McpGetTools => new VsToolResult
            {
                Result = JsonUtils.Serialize(new McpListToolsResult
                    {
                    Tools = [
                        new McpTool
                        {
                            Name = "test.sum",
                            Description = "Сумма двух чисел",
                            InputSchema = new {
                                type = "object",
                                properties = new {
                                    a = new {
                                        type = "number",
                                        description = "Первое число"
                                    },
                                    b = new {
                                        type = "number",
                                        description = "Второе число"
                                    }
                                }
                            }
                        }
                    ]
                })
            },
            BasicEnum.McpCallTool => new VsToolResult
            {
                Result = JsonUtils.Serialize(new MCPToolResult
                {
                    Content = [
                        new () {
                            Type = "text",
                            Text = "42" // - универсальный ответ на все вопросы
                        }
                    ]
                }),
            },
            BuiltInToolEnum.ReadOpenFile => new VsToolResult
            {
                Result = """
                            namespace UIBlazor.Components;

                            public partial class AIChat : TestComponent
                            {
                                private List<ChatMessage> Messages { get; set; } = [];
                            }
                            """
            },
            BuiltInToolEnum.ReadFiles => new VsToolResult
            {
                Result = """
                            File content
                            1 |namespace UIBlazor.Components;
                            2 |
                            3 |public partial class AIChat : TestComponent
                            4 |{
                            5 |    private List<ChatMessage> Messages { get; set; } = [];
                            6 |}
                            """
            },
            BuiltInToolEnum.ApplyDiff => new VsToolResult
            {
                Result = "All replacements is successful."
            },
            _ => original
        };
    }
}
