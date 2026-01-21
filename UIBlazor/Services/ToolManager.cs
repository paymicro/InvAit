using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using UIBlazor.Agents;
using UIBlazor.Models;

namespace UIBlazor.Services;

public class ToolManager(BuiltInAgent builtInAgent)
{
    private readonly ConcurrentDictionary<string, Tool> _registeredTools = new();
    private readonly ConcurrentDictionary<string, AiToolToCall> _pendingTools = new();
    private CancellationTokenSource? _approvalCancellationTokenSource;
    private TaskCompletionSource<bool> _approvalTcs;

    public void RegisterAllTools()
    {
        foreach (var tool in builtInAgent.Tools)
        {
            RegisterTool(tool);
        }
    }

    public IEnumerable<Tool> GetEnabledTools() => _registeredTools.Values.Where(t => t.Enabled);

    public Tool? GetTool(string name)
    {
        return _registeredTools.TryGetValue(name, out var tool) ? tool : null;
    }

    public string GetToolUseSystemInstructions(string promptFromOptions)
    {
        var enabledTools = GetEnabledTools().ToList();
        if (enabledTools.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine(promptFromOptions);
        sb.AppendLine($"Current date: {DateTime.Now.ToString("F")}");
        sb.AppendLine("""

                      You are a function-calling assistant.

                      ## Tool use instructions

                      You have access to several "tools" that you can use at any time to retrieve information and/or perform tasks for the User.

                      You MUST invoke tools exclusively with the following literal syntax; no other format is allowed:
                      <|tool_call_begin|> functions.<toolName>
                      YAML arguments
                      <|tool_call_end|>
                      <|tool_calls_section_end|>

                      Immediately after <|tool_calls_section_end|> - stop generation, no explanatory text.

                      Explanation:
                        <|tool_call_begin|> functions.<toolName>            # function header. toolName - function name.
                        YAML arguments                                      # argument body in YAML
                        <|tool_call_end|>                                   # end of first call
                        <|tool_call_begin|> functions.<toolName>:<index>    # optional second function header. toolName - function name.
                        YAML arguments                                      # argument body in YAML
                        <|tool_call_end|>                                   # end of second call
                        <|tool_calls_section_end|>                          # end of tool call section - end

                      The following tools/fuctions are available to you:

                      """);

        foreach (var tool in enabledTools)
        {
            sb.AppendLine("====");
            sb.AppendLine($"### {tool.Name}");
            sb.AppendLine(tool.Description);
            sb.AppendLine(tool.ExampleToSystemMessage);
            sb.AppendLine();
        }

        sb.AppendLine("""

                      If it seems like the User's request could be solved with the tools, choose the BEST tool for the job based on the user's request and the tool descriptions
                      Then send the tool_calls_section (YOU call the tool, not the user).
                      Do not perform actions with/for hypothetical files. Use tools to deduce which files are relevant.
                      You can call multiple tools in one tool_calls_section.
                      """);
        return sb.ToString();
    }

    private void RegisterTool(Tool tool)
    {
        if (tool.ExecuteAsync == null)
        {
            Debug.WriteLine($"Tool '{tool.Name}' must have an ExecuteAsync function");
        }
        else
        {
            _registeredTools[tool.Name] = tool;
        }
    }
}
