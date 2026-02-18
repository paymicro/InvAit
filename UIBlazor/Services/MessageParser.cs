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
                AppendToken(activeSegment, incomingText.StartsWith('\n')
                    ? incomingText[1..endOfTag] // убираем первый перенос строки после функции, если он есть
                    : incomingText[..endOfTag]);
                Close(activeSegment);
                incomingText = incomingText[endOfTag..];
                continue;
            }

            // Сценарий Б: Открытие
            if (openIdx != -1)
            {
                if (openIdx > 0)
                {
                    AppendToken(activeSegment, incomingText[..openIdx]);
                    Close(activeSegment);
                    incomingText = incomingText[openIdx..];
                    continue;
                }
                else if (activeSegment is { IsClosed: false, Type: SegmentType.Markdown })
                {
                    Close(activeSegment);
                    continue;
                }

                // Находим конец тега '>', чтобы знать, где кончаются параметры (name="...")
                var tagEndIdx = incomingText.IndexOf(">");
                if (tagEndIdx != -1)
                {
                    var consumptionLength = tagEndIdx + 1;
                    AppendToken(activeSegment, incomingText.Substring(0, consumptionLength));
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
            AppendToken(activeSegment, incomingText);
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

    // Вспомогательный буфер для накопления "сырого" текста внутри сегмента
    private readonly StringBuilder _rawAccumulator = new();

    public void AppendToken(ContentSegment segment, string token)
    {
        if (segment.IsClosed || string.IsNullOrEmpty(token)) return;

        _rawAccumulator.Append(token);

        // 1. Определяем тип, если он еще не известен. Теги приходят полными
        if (segment.Type == SegmentType.Unknown)
        {
            var raw = _rawAccumulator.ToString();
            var functionMatch = Regex.Match(token, @"<function name=""(\w+)""(?::(\d+))?>$", RegexOptions.Compiled);
            if (functionMatch.Success)
            {
                segment.Type = SegmentType.Tool;
                segment.TagName = "function";
                segment.ToolName = functionMatch.Groups[1].Value;
            }
            else if (raw.Contains("<plan>"))
            {
                segment.Type = SegmentType.Plan;
                segment.TagName = "plan";
            }
            else if (!string.IsNullOrEmpty(raw))
            {
                segment.Type = SegmentType.Markdown;
            }
        }

        // 2. Обрабатываем текст и разбиваем на линии
        ProcessIncomingText(segment, token);
    }

    private void ProcessIncomingText(ContentSegment segment, string token)
    {
        // Очищаем токен от управляющих тегов, чтобы парсеры их не видели
        var cleanToken = token;
        if (segment.Type != SegmentType.Markdown && !string.IsNullOrEmpty(segment.TagName))
        {
            // Удаляем <function...>, <plan...>, </function>, </plan>
            cleanToken = Regex.Replace(token, $@"^.*<{segment.TagName}[^>]*>(?:\r?\n)?|(?:\r?\n)?<\/{segment.TagName}>.*$", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        if (string.IsNullOrEmpty(cleanToken) && token.Contains('>'))
        {
            return;
        }

        segment.CurrentLine.Append(cleanToken);

        // Если есть перенос строки - фиксируем завершенные линии
        if (segment.Type != SegmentType.Markdown && cleanToken.Contains('\n'))
        {
            var content = segment.CurrentLine.ToString();
            var parts = content.Split('\n');

            for (var i = 0; i < parts.Length; i++)
            {
                segment.Lines.Add(parts[i]);
            }

            segment.CurrentLine.Clear();
            segment.CurrentLine.Append(parts.Last());
        }
    }

    private void Close(ContentSegment segment)
    {
        segment.IsClosed = true;
        // Переносим остаток из буфера в финальные линии, если он там есть
        if (segment.Type != SegmentType.Markdown && segment.CurrentLine.Length > 0)
        {
            segment.Lines.Add(segment.CurrentLine.ToString());
            segment.CurrentLine.Clear();
        }
    }
}
