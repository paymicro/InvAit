namespace Shared.Contracts;

public class VsCodeContext
{
    public const string FilePrefix = "📄";
    public const string DirPrefix = "📁";
    public const string IdeTypeVS = "VS";

    public string SolutionPath { get; set; } = string.Empty;

    public List<string> SolutionFiles { get; set; } = [];

    /// <summary>
    /// Для понимания в какой среде работаем VS или VSCode
    /// </summary>
    public string? IdeType { get; set; } = IdeTypeVS;

    public string? ActiveFilePath { get; set; }

    public string? ActiveFileContent { get; set; }

    public int SelectionStartLine { get; set; }

    public int SelectionEndLine { get; set; }
}