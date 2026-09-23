using System.Text;
using System.Text.Json;
using Shared.Contracts;

namespace UIBlazor.Services;

/// <summary>
/// Formats <see cref="FileContent"/> arrays into display/LLM-ready text with line numbers.
/// Also provides deserialization with graceful fallback for backward compatibility
/// (old sessions in localStorage may contain raw markdown text instead of JSON).
/// </summary>
public static class FileContentFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Tries to deserialize content as a JSON array of <see cref="FileContent"/>.
    /// Returns null if the content is not structured file content (e.g. raw text from bash, old sessions).
    /// Validates that at least one item has a non-empty <see cref="FileContent.Path"/> or non-empty <see cref="FileContent.Lines"/>.
    /// </summary>
    public static List<FileContent>? TryDeserialize(string content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        var trimmed = content.TrimStart();
        if (!trimmed.StartsWith('['))
            return null;

        try
        {
            var result = JsonSerializer.Deserialize<List<FileContent>>(content, JsonOptions);
            if (result is null || result.Count == 0)
                return null;

            // Validate: at least one item must look like a real FileContent
            // (non-empty Path or non-empty Lines), otherwise it's some other JSON array
            var hasValid = false;
            foreach (var fc in result)
            {
                if (!string.IsNullOrEmpty(fc.Path) || (fc.Lines is { Count: > 0 }))
                {
                    hasValid = true;
                    break;
                }
            }

            return hasValid ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Formats <see cref="FileContent"/> array into text with line numbers.
    /// Format:
    /// ### path
    /// ```
    ///    1 | line1
    ///    2 | line2
    /// ```
    /// </summary>
    public static string Format(List<FileContent> files)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < files.Count; i++)
        {
            var fc = files[i];

            if (i > 0)
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }

            sb.AppendLine($"### {fc.Path}");

            if (!string.IsNullOrEmpty(fc.Error))
            {
                sb.AppendLine(fc.Error);
                continue;
            }

            if (fc.Lines is null || fc.Lines.Count == 0)
            {
                sb.AppendLine("```");
                sb.AppendLine("```");
                continue;
            }

            sb.AppendLine("```");

            var startLine = fc.StartLine ?? 1;
            for (var j = 0; j < fc.Lines.Count; j++)
            {
                var num = (startLine + j).ToString().PadLeft(4);
                sb.Append(num);
                sb.Append(" | ");
                sb.AppendLine(fc.Lines[j]);
            }

            sb.AppendLine("```");

            // Truncation notice
            if (fc.TotalLines.HasValue && fc.StartLine is { } sl && sl + fc.Lines.Count - 1 < fc.TotalLines.Value)
            {
                var nextLine = sl + fc.Lines.Count;
                sb.AppendLine($"[... File has {fc.TotalLines.Value} lines total. Showing lines {sl}-{sl + fc.Lines.Count - 1}. Use startLine={nextLine} to read the rest ...]");
            }
            else if (fc.TotalLines.HasValue && fc.StartLine is null && fc.Lines.Count < fc.TotalLines.Value)
            {
                var nextLine = fc.Lines.Count + 1;
                sb.AppendLine($"[... File has {fc.TotalLines.Value} lines total. Showing lines 1-{fc.Lines.Count}. Use startLine={nextLine} to read the rest ...]");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats content for display. If content is structured file JSON, deserializes and formats.
    /// Otherwise returns the original content (backward compatibility).
    /// </summary>
    public static string FormatContent(string content)
    {
        var files = TryDeserialize(content);
        return files is not null ? Format(files) : content;
    }
}
