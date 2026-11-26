using System;
using System.Collections.Generic;
using System.Text.Json;
using Shared.Contracts;

namespace ToolCore;

/// <summary>
/// Parses the <c>edits</c> array of <c>edit_file</c> and — when it cannot — says WHICH edit, WHICH
/// field and WHICH value was wrong.
///
/// It replaces <c>JsonUtils.GetObject&lt;List&lt;DiffEdit&gt;&gt;</c>, which re-serialised the element
/// pretty-printed and bound THAT copy, so a type error answered with a JSON position inside a document
/// the model never sent, naming neither the edit nor the value.
///
/// Shape checks stop here; value semantics stay with <see cref="EditBatch"/> — an edit whose
/// <c>oldStr</c> is JSON null is handed on, because only the batch can say what that would have done.
/// Labels come from <see cref="EditBatch.EditLabel"/>, so a shape refusal and a match refusal are the
/// same sentence with the same index.
/// </summary>
public static class EditPayload
{

    /// <summary>
    /// Reads <c>edits</c> into a batch.
    /// </summary>
    /// <param name="value">The raw payload value (<see cref="JsonElement"/> for a real call).</param>
    /// <param name="edits">
    /// The parsed batch. EMPTY whenever the method returns false — it is published only on success, so
    /// a caller that ignores the return value cannot half-apply a payload already refused.
    /// </param>
    /// <param name="refusal">The model-facing refusal when nothing can be applied; null on success.</param>
    /// <returns>False when the payload could not be read as a list of edits.</returns>
    public static bool TryParse(object? value, out List<DiffEdit> edits, out string? refusal)
    {
        edits = new List<DiffEdit>();
        refusal = null;

        // An ABSENT field is not an empty one: the array is required, so a missing one is a refusal
        // while `[]` is a legal empty batch the caller reports in its own words.
        if (value is null)
        {
            refusal = "'edits' is missing: send a JSON array of edits, " +
                      "e.g. [{\"oldStr\": \"line to find\", \"newStr\": \"replacement\"}]. Nothing was changed.";
            return false;
        }

        if (!JsonShape.TryAsElement(value, "edits", "Nothing was changed.", out var element, out var document, out refusal))
        {
            return false;
        }

        // Published to `edits` only on success — see the parameter documentation.
        var parsed = new List<DiffEdit>();

        try
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                refusal = $"'edits' must be a JSON array of edit objects, received {JsonShape.Describe(element)}. Nothing was changed.";
                return false;
            }

            var total = element.GetArrayLength();
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                index++;

                // A JSON null INSIDE the array is legal: it becomes a null element and EditBatch
                // refuses it PER EDIT, so the siblings still apply.
                if (item.ValueKind == JsonValueKind.Null)
                {
                    parsed.Add(null!);
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    refusal = $"edit {index} of {total}: must be a JSON object with 'oldStr' and 'newStr', " +
                              $"received {JsonShape.Describe(item)}. Nothing was changed.";
                    return false;
                }

                if (!TryReadEdit(item, index, total, out var edit, out refusal))
                {
                    return false;
                }

                parsed.Add(edit);
            }

            edits = parsed;
            return true;
        }
        finally
        {
            document?.Dispose();
        }
    }

    /// <summary>
    /// One edit object. Field types are checked in the ORDER of <see cref="DiffEdit"/>, and every
    /// check names the field and the literal — a refusal the model can act on without guessing.
    /// </summary>
    private static bool TryReadEdit(JsonElement item, int index, int total, out DiffEdit edit, out string? refusal)
    {
        edit = new DiffEdit();
        refusal = null;

        // Read the hint FIRST so every message can carry it: "edit 1 of 1 (line 118)" is what lets the
        // model re-send only this edit. Its own failure already carries the label.
        if (!TryReadApproximateLine(item, out var approximateLine, out refusal))
        {
            refusal = $"edit {index} of {total}: {refusal}";
            return false;
        }

        // One label, one owner: `EditBatch.EditLabel` is what a MATCH refusal prints, so the hint has
        // to be set before asking it.
        edit.ApproximateLine = approximateLine;

        var label = EditBatch.EditLabel(index - 1, total, edit);

        if (!TryReadText(item, "oldStr", label, out var oldStr, out refusal))
        {
            return false;
        }

        // JSON null stays null on purpose: EditBatch checks `is null` before using it and owns the
        // sentence, because a null oldStr silently became [""] and inserted into the first blank line.
        edit.OldStr = oldStr!;

        if (!TryReadText(item, "newStr", label, out var newStr, out refusal))
        {
            return false;
        }

        edit.NewStr = newStr!;

        if (!TryReadReplaceAll(item, label, out var replaceAll, out refusal))
        {
            return false;
        }

        edit.ReplaceAll = replaceAll;
        return true;
    }

    /// <summary>
    /// The optional line hint. The rule is in <see cref="JsonShape.ReadLineNumber"/>; only the sentence
    /// is here, because an edit has its own tail. Bounded by <see cref="int.MaxValue"/> — the largest
    /// value the field can hold — since a hint addresses a line in a file of unknown size.
    /// </summary>
    private static bool TryReadApproximateLine(JsonElement item, out int? value, out string? refusal)
    {
        refusal = null;

        var read = JsonShape.ReadLineNumber(item, "approximateLine", int.MaxValue, out value, out var raw);
        if (read is JsonShape.LineNumberRead.Absent or JsonShape.LineNumberRead.Ok)
        {
            return true;
        }

        // One sentence, stated as a RANGE: a hint is checked by the applier, not served by the host, so
        // "which kind of bad number" would change nothing the model does.
        value = null;
        refusal = $"'approximateLine' must be a whole line number written as plain digits, or null, " +
                  $"received {JsonShape.Describe(raw)}. Nothing was changed.";
        return false;
    }

    private static bool TryReadReplaceAll(JsonElement item, string label, out bool? value, out string? refusal)
    {
        value = null;
        refusal = null;

        if (!JsonShape.TryGetProperty(item, "replaceAll", out var part) || part.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (part.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = part.GetBoolean();
            return true;
        }

        // The schema declares a union with null, so all three spellings are legal. This refuses the
        // WHOLE batch: dropping another edit's flag would rewrite one occurrence where the model asked
        // for all of them.
        refusal = $"{label}: 'replaceAll' must be true, false or null, received {JsonShape.Describe(part)}. Nothing was changed.";
        return false;
    }

    private static bool TryReadText(JsonElement item, string field, string label, out string? value, out string? refusal)
    {
        value = null;
        refusal = null;

        if (!JsonShape.TryGetProperty(item, field, out var part) || part.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (part.ValueKind == JsonValueKind.String)
        {
            value = part.GetString();
            return true;
        }

        // A number or an object here would otherwise reach SplitEditText through a substituted default
        // and apply something the model never wrote.
        refusal = $"{label}: '{field}' must be a string, received {JsonShape.Describe(part)}. Nothing was changed.";
        return false;
    }
}
