using Shared.Contracts;

namespace ToolCore.Tests;

/// <summary>
/// Shape parsing of the <c>edits</c> array.
///
/// Every case here is a refusal the MODEL reads, so each test asserts the sentence, not just the
/// boolean: a refusal that names nobody and nothing (and especially one quoting a line number of a
/// document the model never sent) sends it into a retry loop that eventually breaks a file.
/// </summary>
public class EditPayloadTests
{
    /// <summary>
    /// A payload with the shapes a real call has: escaped backticks and quotes inside the text,
    /// a long line, an integer hint and an explicit <c>false</c>. This is the payload the host
    /// refused with "The JSON value could not be converted to System.Nullable`1[System.Boolean]"
    /// while the offending value was an empty string in ANOTHER attempt — the message named
    /// neither the field nor the value, so the retry was a guess.
    /// </summary>
    private const string ValidPayload = """
        [{"approximateLine":118,"newStr":"- **Tool Calling:** `strict: true` \u2014 the default is SENT as `null`, not by omission. [DefaultValue] declares `null` while the type stays non-nullable; `approximateLine` 118 keeps the hint.","oldStr":"- **Tool Calling:** `strict: true` \u2014 the default is SENT as `null`, not by omission.","replaceAll":false}]
        """;

    private static bool Parse(string editsJson, out List<DiffEdit> edits, out string? refusal)
        => EditPayload.TryParse(JsonDocument.Parse(editsJson).RootElement, out edits, out refusal);

    #region A valid batch is not refused

    [Fact]
    public void TryParse_RealWorldPayload_IsReadWithoutRefusal()
    {
        // The regression: a payload whose ONLY oddity is `replaceAll: false` (a legal boolean) was
        // answered with a JSON-conversion error. Nothing in this payload is wrong, so anything but
        // a clean parse here is the bug the model pays for with a wasted turn.
        Assert.True(Parse(ValidPayload, out var edits, out var refusal), refusal);

        var edit = Assert.Single(edits);
        Assert.Equal(118, edit.ApproximateLine);
        Assert.False(edit.ReplaceAll);
        Assert.Contains("approximateLine` 118 keeps the hint", edit.NewStr);
        Assert.StartsWith("- **Tool Calling:**", edit.OldStr);
    }

    [Fact]
    public void TryParse_ReplaceAllNull_IsReadAsNoFlag()
    {
        // The schema declares ["boolean","null"], so null is a legal spelling of "not set" and must
        // survive as null — `EditBatch` reads `is true`, so null means "exactly one occurrence".
        Assert.True(Parse("""[{"oldStr":"a","newStr":"b","replaceAll":null}]""", out var edits, out var refusal), refusal);
        Assert.Null(Assert.Single(edits).ReplaceAll);
    }

    [Fact]
    public void TryParse_EmptyArray_IsAnEmptyBatch_NotAParseFailure()
    {
        // An empty batch and an unreadable one are different refusals and are reported by the
        // caller ("'edits' is empty: nothing to apply.").
        Assert.True(Parse("[]", out var edits, out var refusal), refusal);
        Assert.Empty(edits);
    }

    [Theory]
    [InlineData("""[{"oldStr":"a","newStr":"b","approximateLine":118}]""", 118)]
    [InlineData("""[{"oldStr":"a","newStr":"b","approximateLine":1}]""", 1)]
    [InlineData("""[{"oldStr":"a","newStr":"b","approximateLine":-3}]""", -3)]
    public void TryParse_IntegralNumberHint_IsAccepted(string json, int expected)
    {
        // A hint written as digits is a hint, whatever its sign. `118.0` and `1e2` are refused in the
        // theory below: the tolerance they needed cost a hand-written number grammar whose first draft
        // turned `1.5e1` into 150 — a silent off-by-ten in the line an edit lands on.
        Assert.True(Parse(json, out var edits, out var refusal), refusal);
        Assert.Equal(expected, Assert.Single(edits).ApproximateLine);
    }

    [Theory]
    [InlineData("""[{"oldStr":"a","newStr":"b","approximateLine":1e400}]""")]
    [InlineData("""[{"oldStr":"a","newStr":"b","approximateLine":2147483648}]""")]
    [InlineData("""[{"oldStr":"a","newStr":"b","approximateLine":1e10}]""")]
    public void TryParse_HintOutsideInt32OrUnparseable_IsRefused(string json)
    {
        // An exponent, and a plain integer wider than `int`, are refused by one sentence: a hint has no
        // consumer that could act on the difference.
        Assert.False(Parse(json, out _, out var refusal));
        Assert.StartsWith("edit 1 of 1: 'approximateLine' must be a whole line number written as plain digits, or null", refusal);
        Assert.DoesNotContain("Infinity", refusal);
    }

    [Fact]
    public void TryParse_NullOldStr_IsLeftToEditBatch()
    {
        // This layer can say WHAT came in, not WHY it is unrecoverable. EditBatch owns the
        // explanation ("oldStr is missing", with what that would have done to the file), and its
        // check is `is null` — so the null must reach it untouched.
        Assert.True(Parse("""[{"oldStr":null,"newStr":"b"}]""", out var edits, out var refusal), refusal);
        Assert.Null(Assert.Single(edits).OldStr);
    }

    #endregion

    #region Refusals name the edit, the field and the value

    [Theory]
    [InlineData("\"\"", "'replaceAll' must be true, false or null, received the string \"\". Nothing was changed.")]
    [InlineData("\"yes\"", "'replaceAll' must be true, false or null, received the string \"yes\". Nothing was changed.")]
    [InlineData("1", "'replaceAll' must be true, false or null, received the number 1. Nothing was changed.")]
    [InlineData("{}", "'replaceAll' must be true, false or null, received an object {}. Nothing was changed.")]
    public void TryParse_ReplaceAllOfAnotherType_NamesTheValueItReceived(string literal, string expectedTail)
    {
        // `""` is the case that produced the report: an EMPTY STRING is not a boolean, and the host
        // answered with a JSON-conversion error about `Nullable<bool>` — true, but useless, because
        // it never said which value it choked on.
        var json = $$"""[{"oldStr":"a","newStr":"b","replaceAll":{{literal}}}]""";

        Assert.False(Parse(json, out _, out var refusal));
        Assert.Equal($"edit 1 of 1: {expectedTail}", refusal);
    }

    [Theory]
    [InlineData("\"\"", "'approximateLine' must be a whole line number written as plain digits, or null, received the string \"\". Nothing was changed.")]
    [InlineData("\"118\"", "'approximateLine' must be a whole line number written as plain digits, or null, received the string \"118\". Nothing was changed.")]
    [InlineData("1.5", "'approximateLine' must be a whole line number written as plain digits, or null, received the number 1.5. Nothing was changed.")]
    [InlineData("118.0", "'approximateLine' must be a whole line number written as plain digits, or null, received the number 118.0. Nothing was changed.")]
    [InlineData("1e2", "'approximateLine' must be a whole line number written as plain digits, or null, received the number 1e2. Nothing was changed.")]
    public void TryParse_HintOfAnotherType_IsRefusedInsteadOfRounded(string literal, string expectedTail)
    {
        // `""` here used to be the swallowed System.Text.Json exception. A fractional hint is NOT
        // rounded: rounding edits a neighbour line. A point or an exponent is refused outright — the
        // sentence says how to write it, so the retry is one turn long.
        var json = $$"""[{"oldStr":"a","newStr":"b","approximateLine":{{literal}}}]""";

        Assert.False(Parse(json, out _, out var refusal));
        Assert.Equal($"edit 1 of 1: {expectedTail}", refusal);
    }

    [Theory]
    [InlineData("oldStr", "42", "the number 42")]
    [InlineData("newStr", "[\"x\"]", "an array [\"x\"]")]
    public void TryParse_TextFieldOfAnotherType_NamesTheFieldAndTheValue(string field, string literal, string described)
    {
        var json = $$"""[{"oldStr":"a","newStr":"b","{{field}}":{{literal}}}]""";

        Assert.False(Parse(json, out _, out var refusal));
        Assert.Equal($"edit 1 of 1: '{field}' must be a string, received {described}. Nothing was changed.", refusal);
    }

    [Fact]
    public void TryParse_FailureInASecondEdit_CarriesTheHintSoTheModelCanFindIt()
    {
        // The label is the SAME sentence EditBatch prints for a match failure, including the line
        // hint — the model re-sends exactly one edit by number, and the hint says which line it is.
        Assert.False(Parse("""
            [{"oldStr":"a","newStr":"b"},
             {"approximateLine":42,"oldStr":"c","newStr":"d","replaceAll":"yes"}]
            """, out _, out var refusal));

        Assert.Equal(
            "edit 2 of 2 (line 42): 'replaceAll' must be true, false or null, " +
            "received the string \"yes\". Nothing was changed.",
            refusal);
    }

    [Theory]
    [InlineData("{\"oldStr\":\"a\"}", "'edits' must be a JSON array of edit objects, received an object {\"oldStr\":\"a\"}. Nothing was changed.")]
    [InlineData("\"a\"", "'edits' must be a JSON array of edit objects, received the string \"a\". Nothing was changed.")]
    [InlineData("7", "'edits' must be a JSON array of edit objects, received the number 7. Nothing was changed.")]
    public void TryParse_EditsThatIsNotAnArray_NamesTheKindItReceived(string json, string expected)
    {
        Assert.False(Parse(json, out _, out var refusal));
        Assert.Equal(expected, refusal);
    }

    [Fact]
    public void TryParse_NullEdits_AsksForTheArrayWithAnExample()
    {
        // An ABSENT batch is not an empty one: the schema requires the array, so the refusal shows
        // the shape instead of leaving the model to invent it.
        Assert.False(EditPayload.TryParse(null, out var edits, out var refusal));
        Assert.Empty(edits);
        Assert.StartsWith("'edits' is missing: send a JSON array of edits", refusal);
        Assert.Contains("\"oldStr\"", refusal);
        Assert.Contains("Nothing was changed.", refusal);
    }

    [Theory]
    [InlineData("[\"a\"]", "edit 1 of 1: must be a JSON object with 'oldStr' and 'newStr', received the string \"a\". Nothing was changed.")]
    [InlineData("[7]", "edit 1 of 1: must be a JSON object with 'oldStr' and 'newStr', received the number 7. Nothing was changed.")]
    public void TryParse_ElementThatIsNotAnObjectAndNotNull_IsRefusedWithItsPosition(string json, string expected)
    {
        // A string or a number where an edit object belongs cannot be repaired by EditBatch — there
        // is nothing to hand it — so the whole batch is refused by name and position.
        Assert.False(Parse(json, out _, out var refusal));
        Assert.Equal(expected, refusal);
    }

    [Fact]
    public void TryParse_NullElementInsideTheArray_IsKeptAsAPerEditFailure()
    {
        // `[null]` is legal JSON and MUST NOT refuse the call: EditBatch owns that sentence, and the
        // sibling still applies. Refusing here instead would lose a batch the model cannot see was
        // otherwise fine, and would disagree with the VS Code host, which has always reported it
        // per edit.
        Assert.True(Parse("""[null,{"oldStr":"one","newStr":"ONE"}]""", out var edits, out var refusal), refusal);

        Assert.Equal(2, edits.Count);
        Assert.Null(edits[0]);

        var lines = new List<string> { "one" };
        var result = EditBatch.Apply(edits, lines);

        Assert.Equal(1, result.AppliedEdits);
        Assert.Equal("edit 1 of 2: the edit is null. Each edit needs 'oldStr' and 'newStr' strings.",
            Assert.Single(result.Failures));
        Assert.Equal(new[] { "ONE" }, lines);
    }

    [Fact]
    public void TryParse_HugeOffendingValue_IsCutAndMarked()
    {
        // The model must see an END of the literal, not 40 KB of its own text echoed back.
        var huge = new string('x', 500);

        Assert.False(Parse($$"""[{"oldStr":"a","newStr":"b","replaceAll":"{{huge}}"}]""", out _, out var refusal));

        Assert.EndsWith("Nothing was changed.", refusal);
        Assert.Contains("\u2026", refusal);
        Assert.True(refusal.Length < 200, $"the refusal must stay readable: {refusal.Length} chars");
    }

    #endregion

    #region The message must never quote a document the model did not send

    [Theory]
    [InlineData("""[{"replaceAll":""}]""")]
    [InlineData("""[{"approximateLine":""}]""")]
    [InlineData("""[{"oldStr":42,"newStr":"b"}]""")]
    [InlineData("""["not an object"]""")]
    [InlineData("""{"oldStr":"a"}""")]
    public void TryParse_Refusal_NeverQuotesAJsonPathOrAPosition(string json)
    {
        // `JsonUtils.GetObject<List<DiffEdit>>` re-serialised the element with WriteIndented = true
        // and let System.Text.Json report a position inside that PRETTY-PRINTED COPY. On a
        // one-line payload the model was told "LineNumber: 5 | BytePositionInLine: 20" — a place
        // that does not exist in what it sent. No refusal in this path may do that again.
        Assert.False(Parse(json, out _, out var refusal));

        Assert.DoesNotContain("LineNumber", refusal);
        Assert.DoesNotContain("BytePositionInLine", refusal);
        Assert.DoesNotContain("Path: $", refusal);
        Assert.DoesNotContain("could not be converted", refusal);
        Assert.DoesNotContain("System.Nullable", refusal);
    }

    #endregion

    #region The rules that have no visible failure, so they need a test

    [Theory]
    [InlineData("OldStr", "NEWSTR", "ApproximateLine", "ReplaceAll")]
    [InlineData("OLDSTR", "newstr", "approximateline", "replaceall")]
    [InlineData("oldstr", "NewStr", "APPROXIMATELINE", "REPLACEALL")]
    public void TryParse_PropertyNamesAreReadIgnoringCase(string oldStr, string newStr, string hint, string flag)
    {
        // `JsonUtils.GetObject` bound these with `PropertyNameCaseInsensitive = true`, and the approval
        // card reads them with `OrdinalIgnoreCase`. Dropping the fallback is invisible without a test:
        // the card keeps rendering a valid diff, the user APPROVES it, and the host answers
        // "oldStr is missing" — a refusal about a typo nobody made.

        Assert.True(Parse(
            $"[{{\"{oldStr}\":\"a\",\"{newStr}\":\"b\",\"{hint}\":3,\"{flag}\":false}}]",
            out var edits,
            out var refusal), refusal);

        var edit = Assert.Single(edits);
        Assert.Equal("a", edit.OldStr);
        Assert.Equal("b", edit.NewStr);
        Assert.Equal(3, edit.ApproximateLine);
        Assert.False(edit.ReplaceAll);
    }

    [Fact]
    public void TryParse_TwoMistakesInOneEdit_AnswersInDiffEditOrder()
    {
        // The ORDER is a cross-host contract (docs/dev/edit-matching.md): one pass per edit, hint
        // first, then oldStr, then newStr, then replaceAll. Both fields are wrong here, so a
        // per-FIELD implementation would answer `replaceAll` and the model would fix the wrong one.
        Assert.False(Parse("""
            [{"oldStr":42,"newStr":"b","replaceAll":"yes"}]
            """, out _, out var refusal));

        Assert.Equal(
            "edit 1 of 1: 'oldStr' must be a string, received the number 42. Nothing was changed.",
            refusal);
    }

    [Fact]
    public void TryParse_Failure_PublishesNothing()
    {
        // Documented on the out parameter: the batch is assembled locally and published only on
        // success, so a caller that ignores the return value cannot half-apply a refused payload.
        Assert.False(Parse("""
            [{"oldStr":"a","newStr":"b"},{"oldStr":"c","newStr":7}]
            """, out var edits, out var refusal));

        Assert.NotNull(refusal);
        Assert.Empty(edits);
    }

    [Fact]
    public void TryParse_HintPastInt32_IsRefused()
    {
        // An `int` field cannot hold this, and truncating it would edit a line the model did not choose.
        Assert.False(Parse("""
            [{"oldStr":"a","newStr":"b","approximateLine":3000000000}]
            """, out _, out var refusal));

        Assert.Equal(
            "edit 1 of 1: 'approximateLine' must be a whole line number written as plain digits, or null, " +
            "received the number 3000000000. Nothing was changed.",
            refusal);
    }

    [Fact]
    public void TryParse_CutStringLiteral_KeepsBothQuotes()
    {
        // The raw JSON is cut blind, so a long string used to arrive as `"abcdef…` — an opening quote
        // with no end, in the one field the whole sentence hinges on.
        var huge = new string('x', 500);

        Assert.False(Parse(
            "[{\"oldStr\":\"a\",\"newStr\":\"b\",\"approximateLine\":\"" + huge + "\"}]",
            out _, out var refusal));

        Assert.NotNull(refusal);
        Assert.DoesNotContain(huge, refusal);
        Assert.Contains("received the string \"", refusal);
        Assert.Contains("…\"", refusal);
    }

    [Fact]
    public void TryParse_SurrogatePairAtTheCutBoundary_IsNotSplit()
    {
        // A lone surrogate in the refusal is not the text of anything: cutting inside an emoji leaves
        // half a code point. The raw text of the literal is `"` + 58 a's + the pair, so the HIGH
        // surrogate sits at index 59 — the last index a 60-character cut keeps. With 59 a's it sits at
        // 60, outside the cut, and the test passed whether the back-off existed or not.
        var text = new string('a', 58) + "\U0001F600" + new string('b', 100);

        Assert.False(Parse(
            "[{\"oldStr\":\"a\",\"newStr\":\"b\",\"approximateLine\":\"" + text + "\"}]",
            out _, out var refusal));

        Assert.NotNull(refusal);
        Assert.Contains("…", refusal);

        var cut = refusal!.Substring(0, refusal.IndexOf('…'));
        Assert.False(char.IsHighSurrogate(cut[^1]));
        // The guard must have MOVED the cut, not merely happened to land well: a high surrogate at the
        // boundary means the second half of the pair was dropped.
        Assert.EndsWith("a", cut);
    }


    [Fact]
    public void TryParse_LiteralExactlyAtTheBudget_IsNotCut()
    {
        // The boundary itself was unpinned: `MaxLiteralLength` could be changed to 12 — or `<=` to `<`
        // — and every existing assertion still held, because they only compared a 10-character literal
        // (never cut) with a 500-character one (cut to pieces).
        var atBudget = new string('x', 58);          // + the two quotes = 60 characters of raw JSON
        Assert.False(Parse(EditWithHint(atBudget), out _, out var kept));
        Assert.Contains("\"" + atBudget + "\"", kept);
        Assert.DoesNotContain("…", kept);

        // 61 characters: the closing quote is the character that falls off, so the literal now ends
        // with the marker. The value itself still shows — that is what "cut at 60" means.
        var onePast = new string('x', 59);
        Assert.False(Parse(EditWithHint(onePast), out _, out var cut));
        Assert.Contains("…\"", cut);
        Assert.DoesNotContain("\"" + onePast + "\"", cut);
    }

    /// <summary>An edit whose `approximateLine` carries the given text — the field whose value is echoed.</summary>
    private static string EditWithHint(string value)
        => "[{\"oldStr\":\"a\",\"newStr\":\"b\",\"approximateLine\":\"" + value + "\"}]";

    #endregion

    #region The parsed batch is the batch EditBatch applies

    [Fact]
    public void TryParse_ThenApply_AppliesTheBatchTheModelSent()
    {
        // End to end through the seam the host uses: parse → EditBatch. The point is that parsing
        // leaves the values untouched, so the edit lands on the line the model named.
        Assert.True(Parse("""
            [{"approximateLine":2,"oldStr":"beta","newStr":"BETA","replaceAll":false}]
            """, out var edits, out var refusal), refusal);

        var lines = new List<string> { "alpha", "beta", "gamma" };
        var result = EditBatch.Apply(edits, lines);

        Assert.Equal(1, result.AppliedEdits);
        Assert.Equal(1, result.TotalReplacements);
        Assert.Empty(result.Failures);
        Assert.Equal(new[] { "alpha", "BETA", "gamma" }, lines);
    }

    #endregion
}
