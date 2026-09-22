namespace Shared.Contracts;

public class VsContext
{
    public const string IdeTypeVS = "VS";

    public string SolutionPath { get; set; } = string.Empty;

    /// <summary>
    /// Full file paths in the solution (raw, no formatting/emojis).
    /// Formatting is done on the UIBlazor side via <see cref="UIBlazor.Services.SolutionTreeBuilder"/>.
    /// </summary>
    public List<string> SolutionFiles { get; set; } = [];

    /// <summary>
    /// Full paths to project files (e.g. .csproj). Used for project name detection in tree.
    /// </summary>
    public List<string> SolutionProjects { get; set; } = [];

    /// <summary>
    /// Для понимания в какой среде работаем VS или VSCode
    /// </summary>
    public string? IdeType { get; set; } = IdeTypeVS;

    public string? ActiveFilePath { get; set; }

    public string? ActiveFileContent { get; set; }

    public int SelectionStartLine { get; set; }

    public int SelectionEndLine { get; set; }
}