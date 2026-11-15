using System.Diagnostics.CodeAnalysis;
using Shared.Contracts.Mcp;

namespace UIBlazor.Services;

/// <summary>
/// Мок VS-моста для отладки UI без VS (headless-режим).
/// <para>
/// Пока VS-мост недоступен, любой тулз возвращает ошибку <c>WebView2 API not found.</c> —
/// именно по ней распознаётся headless-режим. В нём мок подменяет ответ на правдоподобный,
/// чтобы можно было работать с UI без бэкенда.
/// </para>
/// </summary>
[ExcludeFromCodeCoverage]
public class HeadlessMocker
{
    /// <summary>
    /// Искусственная задержка headless-тулза (мс). Нужна, чтобы увидеть в UI обратный отсчёт
    /// <c>ToolTimeoutBadge</c> и итоговое время выполнения, когда реального исполнения нет.
    /// <para>
    /// Обычная статическая настройка: тесты ставят её в 0 (см. <c>TestSetup</c>).
    /// Задержка попадает внутрь измеряемого интервала исполнения (start → result), поэтому
    /// тайминговые тесты не должны её видеть.
    /// </para>
    /// </summary>
    public static int DebugToolDelayMs { get; set; } = 5_000;

    /// <summary>
    /// Мок-ответ моста: в headless-режиме может сымитировать «медленный» тулз
    /// (<see cref="DebugToolDelayMs"/>) и подменить ответ на правдоподобный.
    /// <para>
    /// Вызывать ДО присвоения результата: искусственное ожидание должно попасть внутрь
    /// измеряемого интервала исполнения, как у реального медленного тулза.
    /// </para>
    /// </summary>
    public static async Task<VsToolResult> GetVsToolResultAsync(VsToolResult original, CancellationToken cancellationToken)
    {
        // Реальный ответ боевого VS — не трогаем и ничего не задерживаем:
        // иначе DEBUG-сборка внутри VS тормозила бы каждый тулз на DebugToolDelayMs.
        if (!IsHeadless(original))
            return original;

        if (DebugToolDelayMs > 0)
            await Task.Delay(DebugToolDelayMs, cancellationToken);

        return GetVsToolResult(original);
    }

    /// <summary>
    /// Тулз не дошёл до VS (UI запущен без него) — только такие ответы подменяются моком.
    /// </summary>
    private static bool IsHeadless(VsToolResult original)
        => !original.Success && original.ErrorMessage == HeadlessErrorMarker;

    /// <summary>Признак отсутствия VS-моста в ответе тулза.</summary>
    private const string HeadlessErrorMarker = "WebView2 API not found.";

    /// <summary>
    /// Синхронный мок-ответ (без имитации задержки). Вызывающие, которым она не нужна
    /// (настройки MCP, скиллы, тест-прогоны), используют этот вариант; путь исполнения
    /// тулзов использует <see cref="GetVsToolResultAsync"/>.
    /// </summary>
    public static VsToolResult GetVsToolResult(VsToolResult original)
    {
        if (!IsHeadless(original))
            return original;

        return original.Name switch
        {
            BasicEnum.ReadMcpSettingsFile => new VsToolResult
            {
                Result = JsonUtils.Serialize(new McpSettingsFile
                {
                    Mcp = new Dictionary<string, McpServerJsonEntry>
                    {
                        {
                            "ui-test",
                            new McpServerJsonEntry
                            {
                                Type = "local",
                                Command = ["npx", "-y", "ui-test"],
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
            BasicEnum.GetSkillsMetadata => new VsToolResult
            {
                Result = JsonUtils.Serialize(new List<SkillMetadata>
                {
                    new()
                    {
                        Name = "test-skill",
                        Description = "Test skill for headless UI testing without VS"
                    }
                })
            },
            BasicEnum.ReadSkillContent => new VsToolResult
            {
                Result = JsonUtils.Serialize(new SkillContent
                {
                    Name = "test-skill",
                    Description = "Test skill for headless UI testing without VS",
                    Content = """
                             # Test Skill

                             This is a test skill for running the UI without Visual Studio.

                             ## Instructions

                             1. Verify that skills metadata is loaded.
                             2. Verify that skill content activation works via slash command.
                             """,
                    Files = ["references/api-spec.md", "scripts/build.sh"]
                })
            },
            BuiltInToolEnum.ReadOpenFile => new VsToolResult
            {
                Result = """
                            namespace UIBlazor.Components;
                            // just comment for open file
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
                            2 |// just comment for read file
                            3 |public partial class AIChat : TestComponent
                            4 |{
                            5 |    private List<ChatMessage> Messages { get; set; } = [];
                            6 |}
                            """
            },
            BuiltInToolEnum.EditFile => new VsToolResult
            {
                Result = "All replacements is successful."
            },
            _ => original
        };
    }
}
