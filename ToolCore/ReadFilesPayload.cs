using System;
using System.Collections.Generic;
using System.Text.Json;
using Shared.Contracts;

namespace ToolCore;

/// <summary>
/// Parses the <c>files</c> array of <c>read_files</c> into the requests that will actually be served:
/// normalised, deduplicated, and refused with a sentence that names the entry, the field and the value.
///
/// It exists because the payload used to go through <c>JsonUtils.GetObject&lt;List&lt;ReadFileParams&gt;&gt;</c>,
/// which swallowed the binding error and returned <c>null</c>, so the host could only answer with a
/// fixed guess that asserted a cause it did not know — a model that sent <c>"startLine": "120"</c> was
/// told to check its <c>path</c>.
///
/// What it decides, so the host does not have to: a non-object entry is refused before any file is
/// touched; <c>null</c>, <c>0</c> and negatives all mean "no range" and collapse to ONE number before
/// the dedup key, because two spellings of one read must not produce two entries; the key is the whole
/// request, since two ranges of one file are two different reads; and a range that is not a plain line
/// number is refused rather than folded to "no range", which answered with the entire file.
/// </summary>
public static class ReadFilesPayload
{
    /// <summary>The prefix every refusal from this parser carries: one sentence, one owner.</summary>
    private const string Prefix = "Failed to read 'files': ";

    /// <summary>What a refusal promises the model about the outside world.</summary>
    private const string NothingRead = "Nothing was read.";

    /// <summary>
    /// The largest line number either field accepts. These are positions in a file a person is looking
    /// at, not arbitrary integers: past this is a hallucinated coordinate, and <c>read_files</c> could
    /// not serve it anyway. Shared with the VS Code host (<c>MAX_LINE_NUMBER</c> in <c>fileOps.ts</c>).
    /// </summary>
    public const int MaxLineNumber = 10_000;

    /// <summary>Reads <c>files</c> into the list of reads to serve.</summary>
    /// <param name="value">The raw payload value (<see cref="JsonElement"/> for a real call).</param>
    /// <param name="files">
    /// Normalised and deduplicated, in request order. EMPTY whenever the method returns false — the list
    /// is published only on success, so a caller that ignores the return value cannot read half a
    /// payload this method already refused. An EMPTY array is a successful parse with nothing in it;
    /// the caller words that.
    /// </param>
    /// <param name="refusal">The model-facing refusal; null on success.</param>
    /// <returns>False when the payload could not be read as a list of reads.</returns>
    public static bool TryParse(object? value, out List<ReadFileParams> files, out string? refusal)
    {
        files = [];
        refusal = null;

        if (value is null)
        {
            refusal = Prefix + "the value is missing. Send a JSON array of reads, " +
                      "e.g. [{\"path\": \"src/File.cs\"}]. " + NothingRead;
            return false;
        }

        if (!JsonShape.TryAsElement(value, "files", NothingRead, out var element, out var document, out refusal))
        {
            return false;
        }

        var parsed = new List<ReadFileParams>();

        try
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                refusal = Prefix + $"expected a JSON array, received {JsonShape.Describe(element)}. " + NothingRead;
                return false;
            }

            var total = element.GetArrayLength();
            var index = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in element.EnumerateArray())
            {
                index++;

                if (!TryReadEntry(item, index, total, out var entry, out refusal))
                {
                    return false;
                }

                entry.StartLine = entry.StartLine is > 0 ? entry.StartLine : -1;
                entry.LineCount = entry.LineCount is > 0 ? entry.LineCount : -1;

                var key = $"{entry.Path.ToLowerInvariant()}|{entry.StartLine}|{entry.LineCount}";
                if (seen.Add(key))
                {
                    parsed.Add(entry);
                }
            }

            files = parsed;
            return true;
        }
        finally
        {
            document?.Dispose();
        }
    }

    /// <summary>
    /// One entry: a <c>path</c> and two optional integers. Every refusal names the entry with the 1-based
    /// index a reader counts ("entry 2 of 3"), the field, and the literal received.
    /// </summary>
    private static bool TryReadEntry(JsonElement item, int index, int total, out ReadFileParams entry, out string? refusal)
    {
        entry = new ReadFileParams();
        refusal = null;

        var label = $"entry {index} of {total}";

        if (item.ValueKind != JsonValueKind.Object)
        {
            refusal = Prefix + $"{label} must be a JSON object with a 'path', " +
                      $"received {JsonShape.Describe(item)}. " + NothingRead;
            return false;
        }

        // `path` has no null spelling: a read of nowhere is not a request. A blank string is the same
        // mistake as a missing one — it resolved to the solution root and then failed outside the
        // host's own error handling.
        if (!JsonShape.TryGetProperty(item, "path", out var pathPart)
            || pathPart.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(pathPart.GetString()))
        {
            refusal = Prefix + $"{label} has no 'path'. Each entry needs a 'path' string. " + NothingRead;
            return false;
        }

        entry.Path = pathPart.GetString()!;

        if (!TryReadRange(item, "startLine", label, out var startLine, out refusal))
        {
            return false;
        }

        if (!TryReadRange(item, "lineCount", label, out var lineCount, out refusal))
        {
            return false;
        }

        entry.StartLine = startLine;
        entry.LineCount = lineCount;
        return true;
    }

    /// <summary>
    /// An optional bound: null and an absent key both mean "no bound", and anything that is not a plain
    /// line number in range is refused rather than guessed at, because guessing here reads the whole
    /// file. One sentence for every failure, stated as a RANGE — telling "not a number" from "too big"
    /// would buy the model nothing, since both need the same correction.
    /// </summary>
    private static bool TryReadRange(JsonElement item, string field, string label, out int? value, out string? refusal)
    {
        refusal = null;

        var read = JsonShape.ReadLineNumber(item, field, MaxLineNumber, out value, out var raw);
        if (read is JsonShape.LineNumberRead.Absent or JsonShape.LineNumberRead.Ok)
        {
            return true;
        }

        value = null;
        refusal = Prefix + $"{label}: '{field}' must be a whole line number between 1 and {MaxLineNumber}, " +
                  $"written as plain digits, or null for no range, received {JsonShape.Describe(raw)}. " + NothingRead;
        return false;
    }
}
