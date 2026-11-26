using System;
using System.Text.Json;

namespace ToolCore;

/// <summary>
/// The shape readers and wording helpers shared by <see cref="EditPayload"/> and
/// <see cref="ReadFilesPayload"/>. Both tools answer a bad payload with the same kind of sentence —
/// which entry, which field, which literal — and a refusal is the only thing the model sees of a
/// rejected call, so the wording is production text and lives in one place.
/// </summary>
internal static class JsonShape
{
    /// <summary>How much of an offending value is echoed back. A whole 40 KB oldStr is not a message.</summary>
    internal const int MaxLiteralLength = 60;

    /// <summary>
    /// What a JSON number read as a line number turned out to be. The classification is here; the
    /// SENTENCE belongs to the caller, because the two tools have different tails ("Nothing was read."
    /// / "Nothing was changed.") and different entry labels.
    /// </summary>
    internal enum LineNumberRead
    {
        /// <summary>The field is absent or JSON null — the caller decides what that means.</summary>
        Absent,

        /// <summary>A plain integer within the allowed line number.</summary>
        Ok,

        /// <summary>Anything else: not a number, not a plain integer (`1.5`, `1e2`), or out of range.</summary>
        Invalid,
    }

    /// <summary>
    /// A property by name, IGNORING CASE — the lookup the approval card also performs, so `{"OldStr": …}`
    /// renders as a valid diff and must not then be refused as "oldStr is missing".
    /// <para>
    /// The value-kind test comes FIRST because <c>JsonElement.TryGetProperty</c> THROWS for an array, a
    /// string, a number, a boolean or null — a guard placed after it could never run.
    /// </para>
    /// </summary>
    internal static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Reads an optional LINE NUMBER (<c>int?</c> in the contract, so <c>["integer","null"]</c> in the
    /// schema). <paramref name="max"/> is the largest number the caller can serve;
    /// <paramref name="raw"/> receives the offending element so the caller can echo it.
    /// <para>
    /// The literal must be a PLAIN INTEGER, and <c>TryGetInt32</c> is the test: it parses the raw text
    /// and accepts nothing but an optionally-signed run of digits. Deliberately not "parse and ask
    /// whether the value is whole" — every API that reads a fraction ROUNDS first, so `1e-29` becomes
    /// `0` (the whole file, silently) and 29 nines become `1` (one line instead of a refusal). An
    /// integer parse never looks at a fraction, so that hazard is unreachable.
    /// </para>
    /// <para>
    /// The cost: `118.0` and `1e2` are refused, with a sentence saying to write plain digits. Both the
    /// parse failure and the bound answer with one sentence naming a RANGE, so a value that was spelled
    /// fine is never called a typo.
    /// </para>
    /// </summary>
    internal static LineNumberRead ReadLineNumber(
        JsonElement item,
        string field,
        int max,
        out int? value,
        out JsonElement raw)
    {
        value = null;
        raw = default;

        if (!TryGetProperty(item, field, out var part) || part.ValueKind == JsonValueKind.Null)
        {
            return LineNumberRead.Absent;
        }

        raw = part;

        if (part.ValueKind != JsonValueKind.Number
            || !part.TryGetInt32(out var number)
            || number > max)
        {
            return LineNumberRead.Invalid;
        }

        value = number;
        return LineNumberRead.Ok;
    }

    /// <summary>
    /// The offending value as the model sent it, cut to a readable length. The raw JSON is reused
    /// verbatim, so it carries the quotes of a string and the bare spelling of a number and no second
    /// quoting scheme can disagree with what the model sent.
    /// </summary>
    internal static string Describe(JsonElement element)
    {
        var isString = element.ValueKind == JsonValueKind.String;
        var raw = Short(element.GetRawText(), isString);

        return element.ValueKind switch
        {
            JsonValueKind.String => $"the string {raw}",
            JsonValueKind.Number => $"the number {raw}",
            JsonValueKind.True or JsonValueKind.False => $"the boolean {raw}",
            JsonValueKind.Null => "null",
            JsonValueKind.Array => $"an array {raw}",
            _ => $"an object {raw}",
        };
    }

    /// <summary>
    /// Cuts a literal and marks the cut, so a fragment cannot be mistaken for the whole value. A cut
    /// JSON string gets its closing quote back, and a cut never splits a surrogate pair.
    /// </summary>
    internal static string Short(string value, bool isJsonString)
    {
        if (value.Length <= MaxLiteralLength)
        {
            return value;
        }

        var cut = MaxLiteralLength;
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return isJsonString
            ? value.Substring(0, cut) + "…\""
            : value.Substring(0, cut) + "…";
    }

    /// <summary>
    /// The payload value as a <see cref="JsonElement"/>. A real call already holds one, so the round
    /// trip only runs for a caller that built the payload in code — and it keeps one rule set instead
    /// of a second, CLR-typed copy of these checks.
    /// <para>
    /// The message is stripped of its JSON position: that position describes the copy made here, not
    /// the payload the model sent.
    /// </para>
    /// </summary>
    internal static bool TryAsElement(
        object? value,
        string field,
        string tail,
        out JsonElement element,
        out JsonDocument? document,
        out string? refusal)
    {
        element = default;
        document = null;
        refusal = null;

        if (value is JsonElement already)
        {
            element = already;
            return true;
        }

        try
        {
            document = JsonDocument.Parse(JsonUtils.SerializeCompact(value!));
            element = document.RootElement;
            return true;
        }
        catch (Exception e)
        {
            document?.Dispose();
            document = null;
            refusal = $"'{field}' could not be read as JSON: {JsonUtils.DescribeParseError(e)}. {tail}";
            return false;
        }
    }
}
