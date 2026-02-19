namespace Shared.Contracts;

public class VsCodeContext
{
    public List<string> SolutionFiles { get; set; } = [];

    public string ActiveFilePath { get; set; }

    public string ActiveFileContent { get; set; }

    public int SelectionStartLine { get; set; }

    public int SelectionEndLine { get; set; }
}