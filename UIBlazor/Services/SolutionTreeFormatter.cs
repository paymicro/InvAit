using System.Globalization;
using System.Text;
using UIBlazor.Models;

namespace UIBlazor.Services;

/// <summary>
/// Formats a <see cref="SolutionTreeEntry"/> tree into a compact, human-readable
/// ASCII tree with file sizes, e.g.:
/// <code>
/// ├─ Program.cs
/// ├─ Agent/
/// │  ├─ ToolExecutor.cs
/// │  └─ SolutionStructure.cs
/// └─ README.md
/// </code>
/// </summary>
public static class SolutionTreeFormatter
{
    private const string BranchMid = "├─ ";
    private const string BranchEnd = "└─ ";
    private const string PipeCont  = "│  ";
    private const string SpaceCont  = "   ";

    /// <summary>
    /// Formats the root entry and its children as a tree string.
    /// If <paramref name="root"/>.Name is empty, only children are printed.
    /// </summary>
    public static string Format(SolutionTreeEntry root, bool showSizes = true)
    {
        if (root == null) return string.Empty;
        return FormatChildren(root.Children, showSizes);
    }

    /// <summary>
    /// Formats a list of sibling entries as a tree string.
    /// </summary>
    public static string FormatChildren(List<SolutionTreeEntry> children, bool showSizes = true)
    {
        if (children == null || children.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        RenderChildren(children, sb, prefix: string.Empty, showSizes);
        return sb.ToString();
    }

    private static void RenderChildren(
        List<SolutionTreeEntry> children,
        StringBuilder sb,
        string prefix,
        bool showSizes)
    {
        for (var i = 0; i < children.Count; i++)
        {
            var isLast = i == children.Count - 1;
            var entry  = children[i];

            sb.Append(prefix);
            sb.Append(isLast ? BranchEnd : BranchMid);
            AppendEntryLine(sb, entry, showSizes);
            sb.Append('\n');

            if (entry.Children.Count > 0)
            {
                var childPrefix = prefix + (isLast ? SpaceCont : PipeCont);
                RenderChildren(entry.Children, sb, childPrefix, showSizes);
            }
        }
    }

    private static void AppendEntryLine(StringBuilder sb, SolutionTreeEntry entry, bool showSizes)
    {
        if (entry.IsDirectory)
        {
            sb.Append(entry.Name);
            sb.Append('/');
        }
        else
        {
            sb.Append(entry.Name);
            if (showSizes && entry.FileSize.HasValue)
            {
                sb.Append(" [");
                sb.Append(FormatSize(entry.FileSize.Value));
                sb.Append(']');
            }
        }
    }

    /// <summary>
    /// Formats a byte count as a human-readable KB string (e.g. "1 KB", "0.5 KB").
    /// </summary>
    public static string FormatSize(long bytes)
    {
        var kb = bytes / 1024.0;
        // Trim trailing zeros: "1.00 KB" → "1 KB", "0.50 KB" → "0.5 KB", "0.14 KB" stays
        var formatted = kb.ToString("0.##", CultureInfo.InvariantCulture);
        return $"{formatted} KB";
    }
}
