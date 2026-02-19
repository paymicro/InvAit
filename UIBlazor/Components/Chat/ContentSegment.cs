namespace UIBlazor.Components.Chat;

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
}
