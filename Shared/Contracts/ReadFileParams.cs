namespace Shared.Contracts;

public class ReadFileParams
{
    public string Name { get; set; } = string.Empty;
    public int StartLine { get; set; } = -1;
    public int LineCount { get; set; } = -1;
}
