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
                var tagEndIdx = incomingText.IndexOf('>');
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
            var functionMatch = FunctionRegex().Match(token);
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
            cleanToken = Regex.Replace(token, $@".*<{segment.TagName}[^>]*>|<\/{segment.TagName}>.*", "", RegexOptions.NonBacktracking);
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
            segment.Lines.Add(segment.CurrentLine.ToString());
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
            ReadFileParams? fileParams = null;

            for (var i = 0; i < toolLines.Count; i++)
            {
                var line = toolLines[i];
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine))
                    continue;

                if (trimmedLine == "start_line")
                {
                    var valLine = toolLines[++i]?.Trim();
                    if (fileParams != null && int.TryParse(valLine, out var startLine))
                    {
                        fileParams.StartLine = startLine;
                    }
                }
                else if (trimmedLine == "line_count")
                {
                    var valLine = toolLines[++i]?.Trim();
                    if (fileParams != null && int.TryParse(valLine, out var lineCount))
                    {
                        fileParams.LineCount = lineCount;
                    }
                }
                else
                {
                    fileParams = new ReadFileParams
                    {
                        Name = trimmedLine,
                        StartLine = -1,
                        LineCount = -1
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
                    var lastResult = result.LastOrDefault().Value?.ToString() ?? string.Empty;
                    if (lastResult.StartsWith(":start_line:"))
                    {
                        diff.StartLine = int.Parse(lastResult.Split(':')[2]);
                        result.Remove($"param{paramIndex}");
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
            for (var i = 0; i < toolLines.Count; i++)
            {
                var line = toolLines[i];
                var devider = line.IndexOf(':');
                if (devider > 1)
                {
                    var argName = line[..devider].Trim();
                    var argValue = line[(devider + 1)..].Trim();

                    if (!string.IsNullOrEmpty(argName))
                    {
                        if (argValue.Length > 2 && argValue.StartsWith('\"') && argValue.EndsWith('\"'))
                        {
                            argValue = argValue[1..^1];
                        }
                        result[argName] = argValue;
                        continue;
                    }
                }
            }
        }
        else // обычные тулзы
        {
            for (var i = 0; i < toolLines.Count; i++)
            {
                var line = toolLines[i];
                result[$"param{++paramIndex}"] = line;
            }
        }

        return result;
    }

    [GeneratedRegex(@"<function name=""([\w-_\.]+)"">$", RegexOptions.Compiled)]
    private static partial Regex FunctionRegex();
}
