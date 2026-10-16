using Microsoft.Extensions.AI;

namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Конвертация подготовленной истории сессии (типизированные WireMessage-объекты в формате
/// Chat Completions) в ChatMessage из Microsoft.Extensions.AI.
/// </summary>
internal static class AfMessageMapper
{
    /// <summary>
    /// messages — WireMessage из ConversationSession.GetFormattedMessages: та же форма,
    /// которую сериализовал легаси-запрос Chat Completions.
    /// </summary>
    public static IReadOnlyList<ChatMessage> Map(IEnumerable<WireMessage> messages)
    {
        var result = new List<ChatMessage>();

        foreach (var wire in messages)
        {
            switch (wire.Role)
            {
                case ChatMessageRole.System:
                    result.Add(new ChatMessage(ChatRole.System, wire.Content ?? string.Empty));
                    break;

                case ChatMessageRole.Assistant when wire.ToolCalls is { Count: > 0 }:
                {
                    var contents = new List<AIContent>();
                    if (!string.IsNullOrEmpty(wire.Content))
                    {
                        contents.Add(new TextContent(wire.Content));
                    }

                    foreach (var tc in wire.ToolCalls)
                    {
                        var args = ParseArguments(tc.Arguments);
                        contents.Add(new FunctionCallContent(tc.Id, tc.Name, args));
                    }

                    result.Add(new ChatMessage(ChatRole.Assistant, contents));
                    break;
                }

                case ChatMessageRole.Assistant:
                    result.Add(new ChatMessage(ChatRole.Assistant, wire.Content ?? string.Empty));
                    break;

                case ChatMessageRole.Tool:
                    result.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(wire.ToolCallId ?? string.Empty, wire.Content)]));
                    break;

                default:
                    result.Add(new ChatMessage(new ChatRole(wire.Role), wire.Content ?? string.Empty));
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Аргументы вызова приходят строкой JSON; FunctionCallContent ожидает словарь.
    /// </summary>
    private static Dictionary<string, object?>? ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments) || arguments == "null")
        {
            return null;
        }

        try
        {
            // DeserializeParameters does not throw: returns an empty dictionary
            var parsed = JsonUtils.DeserializeParameters(arguments);
            return parsed.Count == 0 ? null : parsed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        }
        catch (JsonException)
        {
            // Некорректный JSON аргументов (частичные дельты) — передаём как есть нельзя,
            // поэтому осколки вызова игнорируются; модель получит callId без аргументов.
            return null;
        }
    }
}
