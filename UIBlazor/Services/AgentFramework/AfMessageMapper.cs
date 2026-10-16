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
    /// Возвращает List: тул-цикл добавляет assistant/tool сообщения между итерациями —
    /// копия уже готова, вызывающему не нужен лишний ToList().
    /// </summary>
    public static List<ChatMessage> Map(IEnumerable<WireMessage> messages)
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
    /// Repair-путь как в AfAgentLoop.ParseArgs: непарсируемые args (обрезанные при
    /// сохранении сессии) чинятся — модель видит частичные аргументы вместо пустых {}.
    /// </summary>
    private static Dictionary<string, object?>? ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments) || arguments == "null")
        {
            return null;
        }

        // DeserializeParameters does not throw: returns an empty dictionary
        var parsed = JsonUtils.DeserializeParameters(arguments);
        if (parsed.Count > 0)
        {
            return parsed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        }

        // Пустой parse: невалидный ИЛИ частичный JSON (обрезан при сохранении сессии).
        // Repair what we can so the model still sees the partial arguments instead of {}.
        return JsonUtils.DeserializePartialAnonymousType(arguments, new Dictionary<string, object?>())
            is { Count: > 0 } repaired ? repaired : null;
    }
}
