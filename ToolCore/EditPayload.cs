using System;
using System.Collections.Generic;
using System.Text.Json;
using Shared.Contracts;

namespace ToolCore;

/// <summary>
/// Parses the <c>edits</c> array of <c>edit_file</c> and — when it cannot — says WHICH edit, WHICH
/// field and WHICH value was wrong.
///
/// WHY this type exists, and why it is not <c>JsonUtils.GetObject&lt;List&lt;DiffEdit&gt;&gt;</c>:
/// that call re-serialised the raw element with <c>WriteIndented = true</c> and asked
/// <c>System.Text.Json</c> to bind it. On a type violation STJ throws with a position inside that
/// pretty-printed COPY, and the host passed the text on verbatim:
///
/// <code>
/// Failed to read 'edits': The JSON value could not be converted to
/// System.Nullable`1[System.Boolean]. Path: $[0].replaceAll | LineNumber: 5 | BytePositionInLine: 20.
/// </code>
///
/// The model had sent ONE LINE, so "line 5, byte 20" pointed at a document that never existed; the
/// sentence named no edit, no field beyond the path and, most importantly, not the value it had
/// sent — so it retried with a different guess. Behind that tail sat a hint ("each edit needs
/// 'oldStr' and 'newStr' strings") that ASSERTED a cause the code did not know, on a payload where
/// both fields were present.
///
/// The rules below replace the binding for the SHAPE checks and leave the value semantics where
/// they belong: an edit whose <c>oldStr</c> is JSON null is handed to <see cref="EditBatch"/>,
/// whose message ("oldStr is missing") names a consequence this layer cannot describe.
///
/// Labels follow <see cref="EditBatch"/> — "edit 2 of 3 (line 42)" — so a shape refusal and a match
/// refusal are the same sentence with the same index, and the model can re-send exactly one edit.
/// </summary>
public static class EditPayload
{

    /// <summary>
    /// Reads <c>edits</c> into a batch.
    /// </summary>
    /// <param name="value">The raw payload value (<see cref="JsonElement"/> for a real call).</param>
    /// <param name="edits">
    /// The parsed batch. EMPTY whenever the method returns false — the batch is assembled into a
    /// local list and published only on success, so a caller that ignores the return value cannot
    /// half-apply a payload this method already refused.
    /// </param>
    /// <param name="refusal">The model-facing refusal when nothing can be applied; null on success.</param>
    /// <returns>False when the payload could not be read as a list of edits.</returns>
    public static bool TryParse(object? value, out List<DiffEdit> edits, out string? refusal)
    {
        edits = new List<DiffEdit>();
        refusal = null;

        // An ABSENT field is not the same shape as an empty one: the array is required by the
        // schema, so a missing one is a refusal while `[]` is a legal (empty) batch that the caller
        // reports in its own words.
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

                // A JSON null INSIDE the array is legal and is NOT a shape error: it becomes a null
                // element and EditBatch refuses it PER EDIT ("the edit is null"), so the siblings
                // still apply. Refusing the whole batch here would turn a recoverable call into a
                // wasted turn — and it would disagree with the VS Code host, which has always
                // reported it that way.
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

        // Read the hint FIRST so every message can carry it: "edit 1 of 1 (line 118)" locates the
        // edit in the file, which is what the model needs to re-send only this one.
        if (!TryReadApproximateLine(item, out var approximateLine, out refusal))
        {
            // The hint itself is what failed — its own message already carries the label.
            refusal = $"edit {index} of {total}: {refusal}";
            return false;
        }

        // One label, one owner: `EditBatch.EditLabel` is what a MATCH refusal prints, so building
        // this one by hand put a second copy of the sentence in the same assembly — and
        // docs/dev/edit-matching.md already promises "Label = `EditBatch.EditLabel`". Set the hint
        // first, then ask the shared helper, so a widening of the label cannot split the two.
        edit.ApproximateLine = approximateLine;

        var label = EditBatch.EditLabel(index - 1, total, edit);

        if (!TryReadText(item, "oldStr", label, out var oldStr, out refusal))
        {
            return false;
        }

        // JSON null stays null on purpose: EditBatch owns the sentence for that (a null oldStr
        // silently became [""] and inserted the replacement into the first blank line), and it can
        // say WHY where this layer can only say WHAT. The `!` is this field's contract with the
        // batch, not an assumption — EditBatch checks `is null` before using it.
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
    /// Прочитать необязательное целое: `int?` в контракте, то есть `["integer","null"]` в схеме.
    /// Классификация живёт в <see cref="JsonShape.ReadIntegerOrNull"/>, фраза — здесь: у правки
    /// свой хвост и своя подпись.
    /// </summary>
    private static bool TryReadApproximateLine(JsonElement item, out int? value, out string? refusal)
    {
        refusal = null;

        var read = JsonShape.ReadIntegerOrNull(item, "approximateLine", out value, out var raw);
        if (read is JsonShape.IntegerRead.Absent or JsonShape.IntegerRead.Ok)
        {
            return true;
        }

        // OutOfInt32 сюда же: строка `int` не вместит такое число, и «половина правды» вроде
        // «принято и обрезано до int.MinValue» была бы хуже одного честного отказа.
        value = null;
        refusal = $"'approximateLine' must be an integer or null, received {JsonShape.Describe(raw)}. Nothing was changed.";
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

        // The schema declares a union with null, so all three spellings are legal and anything else
        // is a type error. It refuses the WHOLE batch — applying the edits that happen to parse
        // while silently dropping the flag of another would rewrite one occurrence where the model
        // asked for all of them.
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

        // A number or an object here used to reach SplitEditText through a substituted default and
        // apply something the model never wrote.
        refusal = $"{label}: '{field}' must be a string, received {JsonShape.Describe(part)}. Nothing was changed.";
        return false;
    }
}
