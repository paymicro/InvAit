using System;
using System.Text.Json;

namespace ToolCore;

/// <summary>
/// How a refusal names the value it choked on: the shape readers and the wording helpers shared by
/// <see cref="EditPayload"/> and <see cref="ReadFilesPayload"/>.
///
/// WHY one type. Both tools answer a bad payload with the same kind of sentence — WHICH entry, WHICH
/// field, WHICH literal — and a second copy of that sentence drifts the moment one of the two is
/// widened. A refusal is the only thing the model sees of a rejected call, so this is production
/// text, not diagnostics: two hosts and two tools must not spell the same rule three ways.
/// </summary>
internal static class JsonShape
{
    /// <summary>How much of an offending value is echoed back. A whole 40 KB oldStr is not a message.</summary>
    internal const int MaxLiteralLength = 60;

    /// <summary>
    /// Что оказалось в JSON-числе, которое читали как <c>int?</c>. Классификация — здесь, а ФРАЗА —
    /// у вызывающего: у двух инструментов разные хвосты («Nothing was read.» / «Nothing was
    /// changed.») и разные подписи записи.
    /// </summary>
    internal enum IntegerRead
    {
        /// <summary>Поле отсутствует или JSON null — значение решает вызывающий.</summary>
        Absent,

        /// <summary>Целое в диапазоне <see cref="int"/>, включая интегральное написание вида `118.0`.</summary>
        Ok,

        /// <summary>Не число, либо число с ненулевой дробной частью.</summary>
        NotAnInteger,

        /// <summary>Интегральное, но за пределами <see cref="int"/> — хост не может его представить.</summary>
        OutOfInt32,
    }

    /// <summary>
    /// A property by name, IGNORING CASE — the lookup <c>JsonUtils.GetObject</c> performed with
    /// <c>PropertyNameCaseInsensitive = true</c>, and the one the approval card in
    /// <c>ToolCallBlock</c> performs with <c>OrdinalIgnoreCase</c>.
    /// <para>
    /// Dropping that tolerance would be a real regression, not a tidy-up: <c>{"OldStr": …}</c> bound
    /// fine before, so the card rendered a valid diff, the user APPROVED it, and the host answered
    /// "oldStr is missing" — a refusal about a typo nobody made.
    /// </para>
    /// </summary>
    internal static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        // The fast path: the model usually spells the field the way the schema does.
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
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
    /// Reads an optional integer property (<c>int?</c> in the contract, so <c>["integer","null"]</c>
    /// in the schema). <paramref name="raw"/> receives the offending element so the caller can echo
    /// it; it is <c>default</c> when nothing was found.
    /// <para>
    /// `118.0` is the same number as `118`. <c>System.Text.Json</c> REFUSED that spelling for
    /// <c>int?</c>, so accepting it is a PARITY fix with the VS Code host — which parses with
    /// <c>JSON.parse</c> and never lost the fraction — and not a restoration of behaviour that once
    /// worked. A fractional or out-of-range value is still not a line number: rounding it would
    /// address the wrong line, so it is refused, never rounded.
    /// </para>
    /// <para>
    /// <c>decimal</c>, not <c>double</c>: decimal arithmetic is EXACT, so "the value is a whole
    /// number" is a real test. A binary float would need an equality against <c>Math.Floor</c> — a
    /// comparison that is a coin flip for the values that matter here (<c>0.1 + 0.2</c> is not
    /// <c>0.3</c>) — and <c>double</c> additionally turns an out-of-range literal such as
    /// <c>1e400</c> into <c>Infinity</c> instead of failing the parse.
    /// </para>
    /// </summary>
    internal static IntegerRead ReadIntegerOrNull(JsonElement item, string field, out int? value, out JsonElement raw)
    {
        value = null;
        raw = default;

        if (!TryGetProperty(item, field, out var part) || part.ValueKind == JsonValueKind.Null)
        {
            return IntegerRead.Absent;
        }

        raw = part;

        if (part.ValueKind != JsonValueKind.Number)
        {
            return IntegerRead.NotAnInteger;
        }

        if (part.TryGetInt32(out var number))
        {
            value = number;
            return IntegerRead.Ok;
        }

        if (part.TryGetDecimal(out var exact) && exact == decimal.Truncate(exact))
        {
            if (exact < int.MinValue || exact > int.MaxValue)
            {
                return IntegerRead.OutOfInt32;
            }

            value = (int)exact;
            return IntegerRead.Ok;
        }

        return IntegerRead.NotAnInteger;
    }

    /// <summary>
    /// The offending value as the model sent it, cut to a readable length. `""` and `"yes"` must be
    /// distinguishable at a glance — "received a string" left the model guessing WHICH string.
    /// <para>
    /// The RAW JSON text is reused verbatim: it already carries the quotes of a string and the bare
    /// spelling of a number, so no second quoting scheme can disagree with what the model sent.
    /// </para>
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
    /// Marks a cut literal so the model cannot mistake a fragment for the whole value.
    /// <para>
    /// The cut backs off one character when it would land between the two halves of a surrogate
    /// pair: a lone surrogate in the refusal is not the text of anything, and it is what a 60th
    /// character inside an emoji produces in raw JSON.
    /// </para>
    /// <para>
    /// A cut JSON STRING gets its closing quote back (<paramref name="isJsonString"/>): the raw text
    /// is cut blind, so a long string arrived as `"abcdef…` — an opening quote with no end, in the
    /// one field the whole sentence hinges on.
    /// </para>
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
    /// The payload value as a <see cref="JsonElement"/>. A real call already holds one (the payload
    /// is deserialised into a dictionary of <see cref="JsonElement"/>), so the round trip below only
    /// runs for a caller that built the dictionary in code — and it keeps ONE rule set instead of a
    /// second, CLR-typed copy of these checks.
    /// <para>
    /// It serialises an arbitrary CLR object, which can throw, and an exception escaping a parser
    /// that documents <c>false</c> for "unreadable" would surface as a host fault instead of a
    /// refusal. The message is stripped of its JSON position first: that position describes the copy
    /// this method just made, not the payload the model sent.
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
            document = JsonDocument.Parse(JsonUtils.SerializeCompact(value));
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
