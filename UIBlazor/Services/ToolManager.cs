using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Shared.Contracts;
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

                      You MUST invoke tools exclusively with the following literal syntax:
                      <tool_call_begin> functions.<toolName>
                      Parameters
                      <tool_call_end>

                      Immediately after using any toll - stop generation, no explanatory text.

                      Explanation:
                        <tool_call_begin> functions.<toolName>            # function header. toolName - function name.
                        param1                                              # parameter 1
                        param2                                              # parameter 2
                        <tool_call_end>                                   # end of first call
                        <tool_call_begin> functions.<toolName>            # optional second function header. toolName - function name.
                        param1                                              # parameter 1 of second function.
                        <tool_call_end>                                   # end of second call

                      The following tools/functions are available to you:

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

    public List<AiTool> ParseToolBlock(string content)
    {
        var result = new List<AiTool>();

        // Регулярное выражение адаптировано под гибридный формат:
        var callRegex = new Regex(
            @"<tool_call_begin>\s*functions\.(\w+)(?::(\d+))?\s*(.*?)\s*<tool_call_end>",
            RegexOptions.Singleline);

        foreach (Match callMatch in callRegex.Matches(content))
        {
            var toolName = callMatch.Groups[1].Value;
            var callId = callMatch.Groups[2].Success ? callMatch.Groups[2].Value : Guid.NewGuid().ToString();
            var args = callMatch.Groups[3].Value;
            var arguments = Parse(args);

            result.Add(new AiTool
            {
                Type = "function",
                Id = callId,
                Index = result.Count,
                Function = new AiToolToCall
                {
                    Name = toolName,
                    Arguments = arguments
                }
            });
        }

        return result;
    }

    public Dictionary<string, object> Parse(string input)
    {
        var result = new Dictionary<string, object>();
        var reader = new StringReader(input);
        string? line;
        var paramIndex = 0;
        var namedIndex = 0;

        while ((line = reader.ReadLine()) != null)
        {
            var trimmedLine = line.Trim();

            if (string.IsNullOrEmpty(trimmedLine))
                continue;

            // Начало блока (<<<<<<< SEARCH)
            if (trimmedLine.StartsWith("<<<<<<< SEARCH"))
            {
                var diff = new DiffReplacement();
                var lastResult = result.LastOrDefault().Value?.ToString() ?? string.Empty;
                if (lastResult.StartsWith(":start_line:"))
                {
                    diff.StartLine = int.Parse(lastResult.Split(':')[2]);
                    result.Remove($"param{paramIndex}");
                }
                var search = new List<string>();
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Trim().StartsWith("=======")) break;
                    search.Add(line);
                }
                diff.Search = search;

                var replace = new List<string>();
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Trim().StartsWith(">>>>>>> REPLACE")) break;
                    replace.Add(line);
                }
                diff.Replace = replace;

                result[$"diff{++namedIndex}"] = diff;
            }
            // Обычная строка параметров
            else
            {
                result[$"param{++paramIndex}"] = line;
            }
        }

        return result;
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
