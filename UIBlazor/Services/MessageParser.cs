using UIBlazor.Services.Settings;

namespace UIBlazor.Services;

public partial class MessageParser(IToolManager toolManager) : IMessageParser
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
                _rawAccumulator.Clear();
                activeSegment = new ContentSegment();
                message.Segments.Add(activeSegment);
            }

            var openIdx = FindOpeningTag(incomingText);
            var closeIdx = activeSegment.Type is not SegmentType.Unknown and not SegmentType.Markdown
                           ? incomingText.IndexOf($"</{activeSegment.TagName}>", StringComparison.Ordinal)
                           : -1;

            // Сценарий А: Закрытие
            if (closeIdx != -1 && (openIdx == -1 || closeIdx < openIdx))
            {
                var closingTag = $"</{activeSegment.TagName}>";
                var endOfTag = closeIdx + closingTag.Length;
                AppendToken(activeSegment, incomingText[..endOfTag]);
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

                if (activeSegment is { IsClosed: false, Type: SegmentType.Markdown })
                {
                    Close(activeSegment);
                    continue;
                }

                // Находим конец тега '>', чтобы знать, где кончаются параметры (name="...")
                var tagEndIdx = incomingText.IndexOf('>');
                if (tagEndIdx != -1)
                {
                    var consumptionLength = tagEndIdx + 1;
                    AppendToken(activeSegment, incomingText[..consumptionLength]);
                    if (activeSegment.Type == SegmentType.Tool && !string.IsNullOrEmpty(activeSegment.ToolName))
                    {
                        if (isHistory) // При загрузке истории все тулзы заапрувлены. Чтобы не было ложного Pending.
                        {
                            activeSegment.ApprovalStatus = ToolApprovalStatus.Approved;
                        }
                        else
                        {
                            // ну и сразу ставим статус, чтобы не было гонки между рендером и обновлением статуса после получения всех параметров
                            var mode = toolManager.GetApprovalModeByToolName(activeSegment.ToolName);
                            activeSegment.ApprovalStatus = mode switch
                            {
                                ToolApprovalMode.Ask => ToolApprovalStatus.Pending,
                                ToolApprovalMode.Deny => ToolApprovalStatus.Rejected,
                                _ => ToolApprovalStatus.Approved
                            };
                        }
                    }
                    incomingText = incomingText[consumptionLength..];
                    continue;
                }
            }

            // Сценарий В: Обычный контент
            AppendToken(activeSegment, incomingText);
            incomingText = string.Empty;
        }
    }

    private static int FindOpeningTag(string text)
    {
        var planIdx = text.IndexOf("<plan", StringComparison.Ordinal);
        var funcIdx = text.IndexOf("<function", StringComparison.Ordinal);

        if (planIdx == -1 && funcIdx == -1) return -1;

        // Берем тот, что встретился раньше
        if (planIdx != -1 && (funcIdx == -1 || planIdx < funcIdx))
        {
            return planIdx;
        }
        return funcIdx;
    }

    // Вспомогательный буфер для накопления "сырого" текста внутри сегмента
    private readonly StringBuilder _rawAccumulator = new();

    private void AppendToken(ContentSegment segment, string token)
    {
        if (segment.IsClosed || string.IsNullOrEmpty(token)) return;

        _rawAccumulator.Append(token);

        // 1. Определяем тип, если он еще не известен. Теги приходят полными
        if (segment.Type == SegmentType.Unknown)
        {
            var raw = _rawAccumulator.ToString();
            var functionMatch = FunctionRegex().Match(token);
            if (functionMatch.Success)
            {
                segment.Type = SegmentType.Tool;
                segment.TagName = "function";
                segment.ToolName = functionMatch.Groups["name"].Value;
                return;
            }

            if (raw.Contains("<plan>"))
            {
                segment.Type = SegmentType.Plan;
                segment.TagName = "plan";
                return;
            }

            if (!string.IsNullOrEmpty(raw))
            {
                segment.Type = SegmentType.Markdown;
            }
        }

        // 2. Обрабатываем текст и разбиваем на линии
        ProcessIncomingText(segment, token);
    }

    private static void ProcessIncomingText(ContentSegment segment, string token)
    {
        segment.CurrentLine.Append(token);

        // Если есть перенос строки - фиксируем завершенные линии
        if (segment.Type != SegmentType.Markdown && token.Contains('\n'))
        {
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
    }

    private static void Close(ContentSegment segment)
    {
        segment.IsClosed = true;
        // Переносим остаток из буфера в финальные линии, если он там есть
        if (segment.Type != SegmentType.Markdown && segment.CurrentLine.Length > 0)
        {
            var lastLine = segment.CurrentLine.ToString();
            // только если последняя линия - не закрывающий тег (99,9% случаев)
            if (!lastLine.Trim().Equals($"</{segment.TagName}>", StringComparison.OrdinalIgnoreCase))
            {
                segment.Lines.Add(lastLine.Replace($"</{segment.TagName}>", ""));
            }
            segment.CurrentLine.Clear();
        }
    }

    public Dictionary<string, object> Parse(string toolName, List<string> toolLines)
    {
        var result = new Dictionary<string, object>();
        var paramIndex = 0;
        var namedIndex = 0;

        if (toolName == BuiltInToolEnum.ReadFiles)
        {
            for (var i = 0; i < toolLines.Count; i++)
            {
                var line = toolLines[i].Trim();

                if (string.IsNullOrEmpty(line))
                    continue;

                var match = Regex.Match(line, @"^(?<path>.*?)(?:\s*\[L(?<line>\d+)(?::C(?<count>\d+))?\])?$", RegexOptions.NonBacktracking);
                if (match.Success)
                {
                    var fileParams = new ReadFileParams
                    {
                        Name = match.Groups["path"].Value,
                        StartLine = match.Groups["line"].Success && int.TryParse(match.Groups["line"].Value, out var startLine) ? startLine : -1,
                        LineCount = match.Groups["count"].Success && int.TryParse(match.Groups["count"].Value, out var lineCount) ? lineCount : -1
                    };
                    result[$"file{++paramIndex}"] = fileParams;
                }
            }
        }
        else if (toolName == BuiltInToolEnum.ApplyDiff)
        {
            for (var i = 0; i < toolLines.Count; i++)
            {
                var line = toolLines[i].Trim();

                if (string.IsNullOrEmpty(line))
                    continue;

                // Начало блока (<<<<<<< SEARCH)
                if (line.StartsWith("<<<<<<< SEARCH"))
                {
                    i++;
                    var diff = new DiffReplacement();
                    var options = line[14..].TrimStart();
                    if (options.StartsWith(':'))
                    {
                        diff.StartLine = int.Parse(options.Split(':')[1]);
                    }
                    var search = new List<string>();
                    for (; i < toolLines.Count; i++)
                    {
                        line = toolLines[i].TrimEnd();
                        if (line.StartsWith("======="))
                        {
                            i++;
                            break;
                        }
                        search.Add(line);
                    }
                    diff.Search = search;

                    var replace = new List<string>();
                    for (; i < toolLines.Count; i++)
                    {
                        line = toolLines[i].TrimEnd();
                        if (line.StartsWith(">>>>>>> REPLACE"))
                        {
                            i++;
                            break;
                        }
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
        }
        else if (toolName.StartsWith("mcp__")) // MCP
        {
            foreach (var line in toolLines)
            {
                var devider = line.IndexOf(':');
                if (devider <= 1)
                    continue;
                var argName = line[..devider].Trim();
                var argValue = line[(devider + 1)..].Trim();

                if (string.IsNullOrEmpty(argName))
                    continue;
                if (argValue.Length > 2 && argValue.StartsWith('\"') && argValue.EndsWith('\"'))
                {
                    argValue = argValue[1..^1];
                }
                result[argName] = argValue;
            }
        }
        else // обычные тулзы
        {
            foreach (var line in toolLines)
            {
                result[$"param{++paramIndex}"] = line;
            }
        }

        return result;
    }

    [GeneratedRegex(@"<function name( {0,1})=( {0,1})""(?<name>[\w-_\.]+)"">$", RegexOptions.Compiled | RegexOptions.NonBacktracking)]
    private static partial Regex FunctionRegex();
}
