namespace Shared.Contracts;

public class DiffReplacement
{
    /// <summary>
    /// Начальная линия (с tolerance)
    /// </summary>
    public int StartLine { get; set; } = -1;
    public List<string> Search { get; set; } = [];
    public List<string> Replace { get; set; } = [];
}
