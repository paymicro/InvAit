namespace UIBlazor.Components.Chat;

public enum SegmentType { Unknown, Markdown, Tool, Plan }

public class ContentSegment
{
    public string Id { get; } = Guid.NewGuid().ToString();

    public SegmentType Type { get; internal set; } = SegmentType.Unknown;

    public string TagName { get; internal set; } = string.Empty;

    public string ToolName { get; set; } = string.Empty;

    public ToolApprovalStatus? ApprovalStatus { get; set; } = ToolApprovalStatus.Approved;

    public bool IsClosed { get; internal set; }

    // Список готовых строк для UI-парсеров (DiffView и т.д.)
    public List<string> Lines { get; } = new();

    // Буфер для текущей (недописанной) строки
    public StringBuilder CurrentLine { get; } = new();

    // Вспомогательный буфер для накопления "сырого" текста внутри сегмента
    private StringBuilder _rawAccumulator = new();

    public void AppendToken(string token)
    {
        if (IsClosed || string.IsNullOrEmpty(token)) return;

        _rawAccumulator.Append(token);

        // 1. Определяем тип, если он еще не известен. Теги приходят полными
        if (Type == SegmentType.Unknown)
        {
            var raw = _rawAccumulator.ToString();
            var functionMatch = Regex.Match(token, @"<function name=""(\w+)""(?::(\d+))?>$", RegexOptions.Compiled);
            if (functionMatch.Success)
            {
                Type = SegmentType.Tool;
                TagName = "function";
                ToolName = functionMatch.Groups[1].Value;
            }
            else if (raw.Contains("<plan>"))
            {
                Type = SegmentType.Plan;
                TagName = "plan";
            }
            else if (!string.IsNullOrWhiteSpace(raw))
            {
                Type = SegmentType.Markdown;
            }
        }

        // 2. Обрабатываем текст и разбиваем на линии
        ProcessIncomingText(token);
    }

    private void ProcessIncomingText(string token)
    {
        // Очищаем токен от управляющих тегов, чтобы парсеры их не видели
        string cleanToken = token;
        if (Type != SegmentType.Markdown && !string.IsNullOrEmpty(TagName))
        {
            // Удаляем <function...>, <plan...>, </function>, </plan>
            cleanToken = Regex.Replace(token, $@".*<{TagName}[^>]*>|<\/{TagName}>.*", "", RegexOptions.IgnoreCase);
        }

        if (string.IsNullOrEmpty(cleanToken) && token.Contains(">")) return;

        CurrentLine.Append(cleanToken);

        // Если есть перенос строки - фиксируем завершенные линии
        if (Type != SegmentType.Markdown && cleanToken.Contains('\n'))
        {
            var content = CurrentLine.ToString();
            var parts = content.Split('\n');

            for (var i = 0; i < parts.Length - 1; i++)
            {
                Lines.Add(parts[i]);
            }

            CurrentLine.Clear();
            CurrentLine.Append(parts.Last());
        }
    }

    public void Close()
    {
        IsClosed = true;
        // Переносим остаток из буфера в финальные линии, если он там есть
        if (Type != SegmentType.Markdown && CurrentLine.Length > 0)
        {
            Lines.Add(CurrentLine.ToString());
            CurrentLine.Clear();
        }
    }
}
