using System.Diagnostics.CodeAnalysis;
using Shared.Contracts.Mcp;

namespace UIBlazor.Services;

/// <summary>
/// Для тестов на голом UI без VS
/// </summary>
[ExcludeFromCodeCoverage]
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
                // https://modelcontextprotocol.io/seps/1330-elicitation-enum-schema-improvements-and-standards
                Result = """
                {
                  "tools": [
                    {
                      "name": "get_pull_request",
                      "description": "Get full details of a pull request including active comments, file changes, reviewer status, and merge commit information",
                      "inputSchema": {
                        "type": "object",
                        "properties": {
                          "workspace": {
                            "type": "string",
                            "description": "Project key (e.g., PROJ)"
                          },
                          "repository": {
                            "type": "string",
                            "description": "Repository slug (e.g., my-repo)"
                          },
                          "pull_request_id": {
                            "type": "number",
                            "description": "Pull request ID"
                          },
                          "boolean": {
                            "type": "boolean",
                            "description": "Просто что-то включает"
                          },
                          "color": {
                            "type": "string",
                            "title": "Color Selection",
                            "enum": ["Red", "Green", "Blue"],
                            "default": "Green"
                          },
                          "color_selection": {
                            "type": "array",
                            "title": "Color Selection",
                            "description": "Choose your favorite colors",
                            "minItems": 1,
                            "maxItems": 3,
                            "items": {
                              "type": "string",
                              "enum": ["Red", "Green", "Blue"]
                            },
                            "default": ["Green"]
                          },
                          "search_context": {
                            "type": "object",
                            "properties": {
                              "before": {
                                "type": "array",
                                "items": {
                                  "type": "string"
                                },
                                "description": "Lines before the target to disambiguate"
                              },
                              "after": {
                                "type": "array",
                                "items": {
                                  "type": "string"
                                },
                                "description": "Lines after the target to disambiguate"
                              },
                              "IsEnabled": {
                                "type": "boolean",
                                "description": "Просто что-то включает"
                              }
                            },
                            "description": "Context lines to disambiguate when code_snippet appears multiple times (optional)"
                          }
                        },
                        "required": [
                          "workspace",
                          "repository",
                          "pull_request_id"
                        ]
                      }
                    }
                  ]
                }
                """
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
