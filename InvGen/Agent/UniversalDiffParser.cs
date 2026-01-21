using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace InvGen.Agent;

public record DiffReplacement
{
    /// <summary>
    /// Для unified diff, путь файла
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Начальная линия (с tolerance)
    /// </summary>
    public int StartLine { get; set; }
    public List<string> Search { get; set; } = [];
    public List<string> Replace { get; set; } = [];
}

public class UniversalDiffParser
{
    private const int DefaultTolerance = 2; // ±2 строки для LLM ошибок

    public List<DiffReplacement> Parse(string[] lines)
    {
        var replacements = new List<DiffReplacement>();
        int i = 0;
        while (i < lines.Length)
        {
            // Пропускаем пустые/комментарии
            if (string.IsNullOrWhiteSpace(lines[i]) || lines[i].StartsWith("#")) { i++; continue; }

            // Пробуем парсить Aider-style блок
            if (lines[i].StartsWith("<<<<<<< SEARCH"))
            {
                var rep = ParseAiderBlock(lines, ref i);
                replacements.Add(rep);
                continue;
            }

            // Пробуем парсить Unified Diff hunk
            if (lines[i].StartsWith("diff --git") || lines[i].StartsWith("---"))
            {
                var rep = ParseUnifiedHunk(lines, ref i);
                if (rep != null) replacements.Add(rep);
                continue;
            }

            // Если ничего не подошло — throw или skip
            throw new FormatException($"Unknown diff format at line {i + 1}: {lines[i]}");
        }
        return replacements;
    }

    private DiffReplacement ParseAiderBlock(string[] lines, ref int i)
    {
        if (!lines[i].StartsWith("<<<<<<< SEARCH"))
            throw new FormatException($"Expected '<<<<<<< SEARCH' at {i + 1}");

        i++;
        int startLine = 0;
        if (i < lines.Length && lines[i].StartsWith(":start_line:"))
        {
            var startStr = lines[i].Substring(12).Trim();
            if (int.TryParse(startStr, out var parsed)) startLine = parsed;
            else throw new FormatException($"Invalid :start_line: at {i + 1}");
            i++;
        }

        // Разделитель -------
        if (i >= lines.Length || !lines[i].StartsWith("-------"))
            throw new FormatException($"Expected '-------' at {i + 1}");
        i++;

        // Search lines
        var search = new List<string>();
        while (i < lines.Length && !lines[i].Equals("======="))
        {
            search.Add(lines[i].TrimEnd()); // Trim trailing для tolerance
            i++;
        }
        if (i >= lines.Length) throw new FormatException("Missing '======='");
        i++; // Skip =======

        // Replace lines
        var replace = new List<string>();
        while (i < lines.Length && !lines[i].StartsWith(">>>>>>> REPLACE"))
        {
            replace.Add(lines[i].TrimEnd());
            i++;
        }
        if (i >= lines.Length) throw new FormatException("Missing '>>>>>>> REPLACE'");
        i++; // Skip >>>>>>>

        // Применяем tolerance: В реальном коде здесь бы искали в файле с ±tolerance,
        // но поскольку это парсер diff'а, предполагаем что startLine — hint для поиска.
        // В твоём коде (где применяешь diff) — добавь логику поиска в окне startLine ±2.

        return new DiffReplacement { StartLine = startLine, Search = search, Replace = replace };
    }

    private DiffReplacement? ParseUnifiedHunk(string[] lines, ref int i)
    {
        string? filePath = null;

        // Парсим header: diff --git a/file b/file или --- a/file +++ b/file
        if (lines[i].StartsWith("diff --git"))
        {
            var match = Regex.Match(lines[i], @"diff --git a/(.*) b/(.*)");
            if (match.Success) filePath = match.Groups[1].Value; // Берём original path
            i++;
        }
        if (lines[i].StartsWith("---"))
        {
            filePath = lines[i].Substring(4).Trim(); // --- a/file
            i++;
            if (i < lines.Length && lines[i].StartsWith("+++")) i++; // Skip +++
        }

        // Hunk header: @@ -start,len +start,len @@
        if (!lines[i].StartsWith("@@")) return null; // Не hunk
        var hunkMatch = Regex.Match(lines[i], @"@@ -(\d+),(\d+) \+(\d+),(\d+) @@");
        if (!hunkMatch.Success) throw new FormatException($"Invalid hunk at {i + 1}");
        int startLine = int.Parse(hunkMatch.Groups[1].Value); // Old start
        // int oldLen = int.Parse(hunkMatch.Groups[2].Value);
        // New start/len игнорируем для простоты
        i++;

        // Парсим линии: - удалить, + добавить, ' ' сохранить (context)
        var search = new List<string>();
        var replace = new List<string>();
        while (i < lines.Length && !lines[i].StartsWith("diff --git") && !lines[i].StartsWith("---") && !lines[i].StartsWith("@@"))
        {
            if (lines[i].StartsWith("-"))
                search.Add(lines[i].Substring(1).TrimEnd());
            else if (lines[i].StartsWith("+"))
                replace.Add(lines[i].Substring(1).TrimEnd());
            // else: context — игнорируем для replace, но можно добавить в search для точности
            i++;
        }

        // Tolerance: Аналогично, startLine — это из @@, но в apply-коде ищи ±2 если нужно.

        return new DiffReplacement { FilePath = filePath, StartLine = startLine, Search = search, Replace = replace };
    }

    public int FindInFile(List<string> target, List<string> search, int hint, int tol)
    {
        for (int offset = -tol; offset <= tol; offset++)
        {
            int candidate = hint + offset - 1; // 1-based Adjust
            if (candidate < 0 || candidate + search.Count > target.Count) continue;
            bool match = true;
            for (int j = 0; j < search.Count; j++)
            {
                if (target[candidate + j].TrimEnd() != search[j]) { match = false; break; }
            }
            if (match) return candidate + 1; // actual start
        }
        return -1;
    }
}