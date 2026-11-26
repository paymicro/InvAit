using System;
using System.Collections.Generic;
using System.Text.Json;
using Shared.Contracts;

namespace ToolCore;

/// <summary>
/// Parses the <c>files</c> array of <c>read_files</c> into the requests that will actually be
/// served: normalised, deduplicated, and refused with a sentence that names the entry.
///
/// WHY this type exists. The payload used to go through <c>JsonUtils.GetObject&lt;List&lt;ReadFileParams&gt;&gt;</c>,
/// which re-serialised the raw element with <c>WriteIndented = true</c> and asked
/// <c>System.Text.Json</c> to bind the copy. A model that sent <c>"startLine": "120"</c> — a string,
/// very common — was answered with the CLR type name and a 0-based index into a document it never
/// wrote:
///
/// <code>
/// Failed to read 'files': The JSON value could not be converted to
/// System.Nullable`1[System.Int32]. Path: $[0].startLine. Nothing was read.
/// </code>
///
/// That is the same defect <see cref="EditPayload"/> was created to remove from <c>edits</c>: no
/// entry number a human counts, no field name, no value, and numbers that describe a copy. The model
/// could not tell whether entry 0 or 1 was wrong, never saw the literal it had sent, and got a host
/// internals name for a fixable typo.
///
/// WHAT it decides, so the host does not have to:
/// <list type="bullet">
///   <item>an absent entry and a null one are refused with their 1-based position, before any file is
///   touched — a null entry cannot yield a path or a range, and letting it reach the read loop turned
///   a fixable payload into <c>NULL_REFERENCE</c>;</item>
///   <item><c>null</c>, <c>0</c> and <c>-1</c> all mean "no range" and collapse to ONE number BEFORE
///   the dedup key is computed, because two spellings of the same read must not produce two identical
///   entries in the answer;</item>
///   <item>the dedup key is the WHOLE request (case-insensitive path plus both bounds): two ranges of
///   one file are two different reads, and a path-only key silently dropped the second one;</item>
///   <item>a range that is not an integer is REFUSED, not folded to "no range": folding it answered
///   with the entire file — far more text than was asked for — while the VS Code host refused the
///   call, so the same payload produced two different answers on two hosts.</item>
/// </list>
/// </summary>
public static class ReadFilesPayload
{
    /// <summary>The prefix every refusal from this parser carries: one sentence, one owner.</summary>
    private const string Prefix = "Failed to read 'files': ";

    /// <summary>What a refusal promises the model about the outside world.</summary>
    private const string NothingRead = "Nothing was read.";

    /// <summary>
    /// Reads <c>files</c> into the list of reads to serve.
    /// </summary>
    /// <param name="value">The raw payload value (<see cref="JsonElement"/> for a real call).</param>
    /// <param name="files">
    /// The plan: normalised and deduplicated, in request order. EMPTY whenever the method returns
    /// false — the list is assembled locally and published only on success, so a caller that ignores
    /// the return value cannot read half a payload this method already refused. An EMPTY array is a
    /// successful parse with nothing in it; the caller words that ("No files specified.").
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

        // Published to `files` only on success — see the parameter documentation.
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

                // Normalise BEFORE the key: null, 0 and -1 are one request, and two spellings of it
                // must collapse into one entry — the answer would otherwise contain the same file
                // twice and the model would pay for both.
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
    /// One entry: a <c>path</c> and two optional integers. Every refusal here names the entry with
    /// the 1-based index a reader counts ("entry 2 of 3"), the field, and the literal received.
    /// </summary>
    private static bool TryReadEntry(JsonElement item, int index, int total, out ReadFileParams entry, out string? refusal)
    {
        entry = new ReadFileParams();
        refusal = null;

        var label = $"entry {index} of {total}";

        // An entry that is not an object (or is JSON null) has no path and no range: there is nothing
        // to read, so the batch is refused instead of reporting a file that was never asked for.
        if (item.ValueKind != JsonValueKind.Object)
        {
            refusal = Prefix + $"{label} must be a JSON object with a 'path', " +
                      $"received {JsonShape.Describe(item)}. " + NothingRead;
            return false;
        }

        // `path` is the one field with no null spelling: a read of nowhere is not a request. A blank
        // string is the same mistake as a missing one — the host resolved it to the solution root and
        // then answered "Path cannot be null or empty." from outside its own error handling.
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
    /// An optional bound. The schema offers <c>["integer","null"]</c>, so null and an absent key are
    /// legal and mean "no bound"; anything that is not a whole number is refused rather than guessed
    /// at, because guessing here silently reads the whole file.
    /// </summary>
    private static bool TryReadRange(JsonElement item, string field, string label, out int? value, out string? refusal)
    {
        refusal = null;

        var read = JsonShape.ReadIntegerOrNull(item, field, out value, out var raw);
        switch (read)
        {
            case JsonShape.IntegerRead.Absent:
            case JsonShape.IntegerRead.Ok:
                return true;

            case JsonShape.IntegerRead.OutOfInt32:
                value = null;
                refusal = Prefix + $"{label}: '{field}' is outside the 32-bit integer range, " +
                          $"received {JsonShape.Describe(raw)}. " + NothingRead;
                return false;

            default:
                value = null;
                refusal = Prefix + $"{label}: '{field}' must be an integer or null, " +
                          $"received {JsonShape.Describe(raw)}. " + NothingRead;
                return false;
        }
    }
}
