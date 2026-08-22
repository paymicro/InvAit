namespace UIBlazor.Services;

public class MessageParser : IMessageParser
{
    public void UpdateSegments(string delta, VisualChatMessage message, bool isHistory = false)
    {
        if (string.IsNullOrEmpty(delta))
            return;

        var activeSegment = isHistory ? null : message.Segments.LastOrDefault(s => !s.IsClosed);

        if (isHistory)
        {
            activeSegment = null;
        }

        var incomingText = delta;

        while (!string.IsNullOrEmpty(incomingText))
        {
            if (activeSegment == null || activeSegment.IsClosed)
            {
                activeSegment = new ContentSegment();
                message.Segments.Add(activeSegment);
            }

            var openIdx = incomingText.IndexOf("<plan", StringComparison.Ordinal);
            var closeIdx = activeSegment.Type is not SegmentType.Unknown and not SegmentType.Markdown
                           ? incomingText.IndexOf($"</{activeSegment.TagName}>", StringComparison.Ordinal)
                           : -1;

            if (closeIdx != -1 && (openIdx == -1 || closeIdx < openIdx))
            {
                var closingTag = $"</{activeSegment.TagName}>";
                var endOfTag = closeIdx + closingTag.Length;
                AppendToken(activeSegment, incomingText[..endOfTag]);
                Close(activeSegment);
                incomingText = incomingText[endOfTag..];
                continue;
            }

            if (openIdx != -1)
            {
                if (openIdx > 0)
                {
                    AppendToken(activeSegment, incomingText[..openIdx]);
                    Close(activeSegment);
                    incomingText = incomingText[openIdx..];
                    continue;
                }

                if (activeSegment is { IsClosed: false, Type: SegmentType.Markdown })
                {
                    Close(activeSegment);
                    continue;
                }

                var tagEndIdx = incomingText.IndexOf('>');
                if (tagEndIdx != -1)
                {
                    var consumptionLength = tagEndIdx + 1;
                    AppendToken(activeSegment, incomingText[..consumptionLength]);
                    incomingText = incomingText[consumptionLength..];
                    continue;
                }
            }

            AppendToken(activeSegment, incomingText);
            incomingText = string.Empty;
        }
    }

    private static void ProcessIncomingText(ContentSegment segment, string token)
    {
        segment.CurrentLine.Append(token);

        // Если есть перенос строки - фиксируем завершенные линии
        if (segment.Type == SegmentType.Markdown || !token.Contains('\n'))
            return;

        var content = segment.CurrentLine.ToString();
        var parts = content.Split('\n');

        if (!string.IsNullOrWhiteSpace(parts[0]))
        {
            segment.Lines.Add(parts[0]);
        }

        for (var i = 1; i < parts.Length - 1; i++)
        {
            segment.Lines.Add(parts[i]);
        }

        segment.CurrentLine.Clear();
        segment.CurrentLine.Append(parts.Last());
    }

    private static void AppendToken(ContentSegment segment, string token)
    {
        if (segment.IsClosed || string.IsNullOrEmpty(token))
            return;

        if (segment.Type == SegmentType.Unknown)
        {
            if (token.Contains("<plan>"))
            {
                segment.Type = SegmentType.Plan;
                segment.TagName = "plan";
            }
            else if (!string.IsNullOrEmpty(token))
            {
                segment.Type = SegmentType.Markdown;
            }
        }

        // 2. Обрабатываем текст и разбиваем на линии
        ProcessIncomingText(segment, token);
    }

    private static void Close(ContentSegment segment)
    {
        segment.IsClosed = true;
    }
}
