using UIBlazor.Components.Chat;
using UIBlazor.Services.Settings;

namespace UIBlazor.Services;

public class MessageParser(IToolManager toolManager) : IMessageParser
{
    public void UpdateSegments(string delta, VisualChatMessage message, bool isHistory = false)
    {
        if (string.IsNullOrEmpty(delta))
            return;

        var activeSegment = isHistory ? null : message.Segments.LastOrDefault(s => !s.IsClosed);

        if (isHistory)
        {
            // В истории дельта - это целое неизменное сообщение.
            // Поэтому активный сегмент всегда новый для каждого вызова.
            activeSegment = null;
        }

        var incomingText = delta;

        while (!string.IsNullOrEmpty(incomingText))
        {
            // Логика сегментов
            if (activeSegment == null || activeSegment.IsClosed)
            {
                activeSegment = new ContentSegment();
                message.Segments.Add(activeSegment);
            }

            var openIdx = FindOpeningTag(incomingText, out _);
            var closeIdx = activeSegment.Type is not SegmentType.Unknown and not SegmentType.Markdown
                           ? incomingText.IndexOf($"</{activeSegment.TagName}>")
                           : -1;

            // Сценарий А: Закрытие
            if (closeIdx != -1 && (openIdx == -1 || closeIdx < openIdx))
            {
                var closingTag = $"</{activeSegment.TagName}>";
                var endOfTag = closeIdx + closingTag.Length;
                activeSegment.AppendToken(incomingText[..endOfTag]);
                activeSegment.Close();
                incomingText = incomingText[endOfTag..];
                continue;
            }

            // Сценарий Б: Открытие
            if (openIdx != -1)
            {
                if (openIdx > 0)
                {
                    activeSegment.AppendToken(incomingText[..openIdx]);
                    activeSegment.Close();
                    incomingText = incomingText[openIdx..];
                    continue;
                }
                else if (activeSegment is { IsClosed: false, Type: SegmentType.Markdown })
                {
                    activeSegment.Close();
                    continue;
                }

                // Находим конец тега '>', чтобы знать, где кончаются параметры (name="...")
                var tagEndIdx = incomingText.IndexOf(">");
                if (tagEndIdx != -1)
                {
                    var consumptionLength = tagEndIdx + 1;
                    activeSegment.AppendToken(incomingText.Substring(0, consumptionLength));
                    if (activeSegment.Type == SegmentType.Tool && !string.IsNullOrEmpty(activeSegment.ToolName))
                    {
                        if (isHistory) // При загрузке истории все тулзы заапрувлены. Чтобы не было ложного Pending.
                        {
                            activeSegment.ApprovalStatus = ToolApprovalStatus.Approved;
                        }
                        else
                        {
                            // ну и сразу ставим статус, чтобы не было гонки между рендером и обновлением статуса после получения всех параметров
                            activeSegment.ApprovalStatus = toolManager.GetApprovalModeByToolName(activeSegment.ToolName) == ToolApprovalMode.Manual
                                ? ToolApprovalStatus.Pending
                                : ToolApprovalStatus.Approved;
                        }
                    }
                    incomingText = incomingText.Substring(consumptionLength);
                    continue;
                }
            }

            // Сценарий В: Обычный контент
            activeSegment.AppendToken(incomingText);
            incomingText = string.Empty;
        }
    }

    private static int FindOpeningTag(string text, out string tagName)
    {
        tagName = "";
        var planIdx = text.IndexOf("<plan");
        var funcIdx = text.IndexOf("<function");

        if (planIdx == -1 && funcIdx == -1) return -1;

        // Берем тот, что встретился раньше
        if (planIdx != -1 && (funcIdx == -1 || planIdx < funcIdx))
        {
            tagName = "plan";
            return planIdx;
        }
        tagName = "function";
        return funcIdx;
    }
}
