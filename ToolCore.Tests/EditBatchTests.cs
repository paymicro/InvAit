using Shared.Contracts;

namespace ToolCore.Tests;

/// <summary>
/// The whole <c>edit_file</c> batch orchestration: validation, ordering, counting and the exact
/// sentences the model reads. This code used to live in the VS host (<c>ToolExecutor.EditFileAsync</c>),
/// which cannot be unit-tested (net48 + VS SDK, no DI) — the counting bug below is exactly why it
/// had to move: `default:` in the outcome switch counted a REFUSED edit as applied, so
/// <c>appliedEdits != 0</c> opened the write gate and the model was told "Changes successfully
/// applied" after a batch that changed nothing.
///
/// Every test asserts a model-facing string verbatim on purpose: those strings are the contract
/// with the LLM (which edit failed, on which line, how to fix it), not presentation.
/// </summary>
public class EditBatchTests
{
    private static DiffEdit Edit(string oldStr, string newStr, int? approximateLine = null, bool? replaceAll = null)
        => new() { OldStr = oldStr, NewStr = newStr, ApproximateLine = approximateLine, ReplaceAll = replaceAll };

    [Fact]
    public void Apply_OneAppliedOneNotFound_CountsOnlyTheAppliedEdit()
    {
        var lines = new List<string> { "one", "two", "three" };
        var edits = new List<DiffEdit> { Edit("two", "TWO"), Edit("missing", "x") };

        var result = EditBatch.Apply(edits, lines);
        result.File = "Test.cs";

        Assert.Equal(1, result.AppliedEdits);
        Assert.Equal(1, result.TotalReplacements);
        Assert.Equal(2, result.TotalEdits);
        Assert.True(result.AnyApplied);
        Assert.Equal(new[] { "edit 2 of 2: oldStr not found" }, result.Failures.ToArray());
        Assert.Equal(new[] { "one", "TWO", "three" }, lines);

        // The exact payload the model receives: counters, then one line per failed edit.
        Assert.Equal(
            "Changes successfully applied to Test.cs. 1/2 edits, 1 replacements." +
            "\nFailed edits:\n - edit 2 of 2: oldStr not found",
            result.FormatReport(dryRun: false));
    }

    [Fact]
    public void Apply_EveryEditFails_ReportsNoValidReplacements()
    {
        // The regression this type exists for: with a REFUSED outcome falling into `default:`,
        // AppliedEdits became 1 here, AnyApplied turned true and the host rewrote the file.
        var lines = new List<string> { "one" };
        var edits = new List<DiffEdit> { Edit("nope", "x"), Edit("nada", "y") };

        var result = EditBatch.Apply(edits, lines);
        result.File = "Test.cs";

        Assert.False(result.AnyApplied);
        Assert.Equal(0, result.AppliedEdits);
        Assert.Equal(0, result.TotalReplacements);
        Assert.Equal(new[] { "one" }, lines);
        Assert.Equal(
            "No valid replacements in Test.cs." +
            "\nFailed edits:\n - edit 1 of 2: oldStr not found\n - edit 2 of 2: oldStr not found",
            result.FormatReport(dryRun: false));
    }

    [Fact]
    public void Apply_AmbiguousEdit_RefusesAndReportsOneBasedLineNumbers()
    {
        // A search found in several places is REFUSED, not applied to the first one: the model
        // gets the candidate lines in 1-based form so it can add context or set replaceAll.
        var lines = new List<string> { "a();", "b();", "a();" };
        var edits = new List<DiffEdit> { Edit("a();", "c();") };

        var result = EditBatch.Apply(edits, lines);
        result.File = "Test.cs";

        Assert.False(result.AnyApplied);
        Assert.Equal(0, result.TotalReplacements);
        Assert.Equal(new[] { "a();", "b();", "a();" }, lines);
        Assert.Equal(
            new[]
            {
                "edit 1 of 1: oldStr found 2 times (lines 1, 3) — add more context or set replaceAll: true",
            },
            result.Failures.ToArray());
    }

    [Fact]
    public void Apply_NullOldStrAndNullNewStr_AreRefusedPerEdit()
    {
        // OldStr/NewStr are declared non-nullable, but `"oldStr": null` in the request still gives
        // null. SplitEditText(null) returns [""], which legitimately matches the first EMPTY line:
        // without this refusal the edit would "succeed" by pasting newStr into a blank line, and a
        // null newStr would silently replace the matched block with one empty line.
        var lines = new List<string> { "one", "two" };
        var edits = new List<DiffEdit>
        {
            new() { OldStr = null!, NewStr = "x" },
            new() { OldStr = "two", NewStr = null! },
        };

        var result = EditBatch.Apply(edits, lines);

        Assert.False(result.AnyApplied);
        Assert.Equal(new[] { "one", "two" }, lines);
        Assert.Equal(2, result.Failures.Count);
        Assert.Equal("edit 1 of 2: oldStr is missing", result.Failures[0]);
        Assert.Equal(
            "edit 2 of 2: newStr is missing. Send the full replacement text; an empty string leaves " +
            "an empty line, and a lone line cannot be deleted here (use create_file to rewrite the file).",
            result.Failures[1]);
    }

    [Fact]
    public void Apply_ReplaceAllPerEdit_CountsEachOccurrenceAsAReplacement()
    {
        // Replacements are counted SEPARATELY from edits: one edit over three occurrences is
        // "1/1 edits, 3 replacements". The per-edit flag is a bool? — null means false.
        var lines = new List<string> { "x = 1;", "x = 1;", "x = 1;" };
        var edits = new List<DiffEdit> { Edit("x = 1;", "x = 2;", replaceAll: true) };

        var result = EditBatch.Apply(edits, lines);

        Assert.Equal(1, result.AppliedEdits);
        Assert.Equal(1, result.TotalEdits);
        Assert.Equal(3, result.TotalReplacements);
        Assert.Empty(result.Failures);
        Assert.Equal(new[] { "x = 2;", "x = 2;", "x = 2;" }, lines);
    }

    [Fact]
    public void Apply_ReplaceAllNull_MeansFalse_SoADuplicateIsRefused()
    {
        // DiffEdit.ReplaceAll is a bool?: the schema allows `"replaceAll": null`, and null must mean
        // false. A batch-wide force used to exist as well — it was read from a payload key the tool
        // schema never declared, so it was false on every real call and only tests could turn it on.
        // Deleted rather than kept as a switch the model cannot reach.
        var lines = new List<string> { "dup", "dup" };
        var edits = new List<DiffEdit> { Edit("dup", "uniq") };

        var result = EditBatch.Apply(edits, lines);

        Assert.Equal(0, result.AppliedEdits);
        Assert.Equal(0, result.TotalReplacements);
        Assert.Equal(new[] { "dup", "dup" }, lines);
        Assert.Equal(
            "edit 1 of 1: oldStr found 2 times (lines 1, 2) — add more context or set replaceAll: true",
            Assert.Single(result.Failures));
    }

    [Fact]
    public void Apply_OrdersByApproximateLineDescending_AndKeepsTheModelsIndices()
    {
        // Bottom-to-top application means a later edit cannot shift the lines of a pending one.
        // The LABELS keep the model's own 1-based indices: it re-sends only the failed edits, and
        // an index from the sorted copy would point it at the wrong one.
        var lines = new List<string> { "l1", "l2", "l3", "l4" };
        var edits = new List<DiffEdit>
        {
            Edit("missing-1", "x", approximateLine: 1),
            Edit("missing-4", "x", approximateLine: 4),
            Edit("missing-3", "x", approximateLine: 3),
        };

        var result = EditBatch.Apply(edits, lines);

        Assert.Equal(
            new[]
            {
                "edit 2 of 3 (line 4): oldStr not found",
                "edit 3 of 3 (line 3): oldStr not found",
                "edit 1 of 3 (line 1): oldStr not found",
            },
            result.Failures.ToArray());
    }

    [Fact]
    public void Apply_BottomToTop_DoesNotShiftTheLinesOfAPendingEdit()
    {
        // Both edits are found with the same single-line search; applying the LOWER one first is
        // what keeps the upper edit's context intact when the lower one gains lines.
        var lines = new List<string> { "alpha", "beta" };
        var edits = new List<DiffEdit>
        {
            Edit("alpha", "alpha\ninserted", approximateLine: 1),
            Edit("beta", "beta\ntail", approximateLine: 2),
        };

        var result = EditBatch.Apply(edits, lines);

        Assert.Equal(2, result.AppliedEdits);
        Assert.Equal(new[] { "alpha", "inserted", "beta", "tail" }, lines);
    }

    [Fact]
    public void FormatReport_DryRun_SaysWhatWouldChangeAndMakesNoSuccessClaim()
    {
        var lines = new List<string> { "one", "two" };
        var edits = new List<DiffEdit> { Edit("two", "TWO"), Edit("missing", "x") };

        var result = EditBatch.Apply(edits, lines);
        result.File = "Test.cs";

        Assert.Equal(
            "Dry run on Test.cs: 1/2 edits would apply, 1 replacements." +
            "\nFailed edits:\n - edit 2 of 2: oldStr not found",
            result.FormatReport(dryRun: true));
    }

    [Fact]
    public void Apply_NoFailures_ReportHasNoFailureSection()
    {
        var lines = new List<string> { "one" };
        var edits = new List<DiffEdit> { Edit("one", "ONE") };

        var result = EditBatch.Apply(edits, lines);
        result.File = "Test.cs";

        Assert.Empty(result.Failures);
        Assert.Equal("Changes successfully applied to Test.cs. 1/1 edits, 1 replacements.", result.FormatReport(dryRun: false));
    }

    [Fact]
    public void Apply_EmptyBatch_IsZeroedOutAndAnyAppliedIsFalse()
    {
        // The host refuses an empty batch before calling us; the result must still be inert if it
        // ever gets here — no phantom applied edit to open the write gate.
        var lines = new List<string> { "one" };

        var result = EditBatch.Apply([], lines);
        result.File = "Test.cs";

        Assert.False(result.AnyApplied);
        Assert.Equal(0, result.TotalEdits);
        Assert.Empty(result.Failures);
        Assert.Equal("No valid replacements in Test.cs.", result.FormatReport(dryRun: false));
    }

    [Fact]
    public void FormatReport_MixedEndingsNote_StartsItsOwnLineAndDoesNotClaimALocalRewrite()
    {
        var lines = new List<string> { "one" };
        var result = EditBatch.Apply([Edit("one", "ONE")], lines);
        result.File = "Test.cs";

        // SaveFileAsync re-joins EVERY line, so the sentence must not promise that untouched lines
        // kept their terminator — and it opens its own line so it cannot merge into a failure bullet.
        Assert.Equal(
            "Changes successfully applied to Test.cs. 1/1 edits, 1 replacements." +
            "\nNote: the file had mixed line endings; it was rewritten with 'crlf' throughout.",
            result.FormatReport(dryRun: false, normalisedSeparator: "\r\n"));
    }

    [Fact]
    public void FormatReport_DryRun_NeverPrintsTheMixedEndingsNote()
    {
        // Nothing is written on a dry run, so nothing was normalised and there is nothing to warn about.
        var lines = new List<string> { "one" };
        var result = EditBatch.Apply([Edit("one", "ONE")], lines);
        result.File = "Test.cs";

        Assert.DoesNotContain(
            "mixed line endings",
            result.FormatReport(dryRun: true, normalisedSeparator: "\r\n"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_AmbiguityWithManyMatches_TruncatesTheListWithAnEllipsis()
    {
        // The model must know it is NOT seeing every candidate: a silent truncation reads like the
        // full list, and it would try to disambiguate with too little context.
        var lines = new List<string>(Enumerable.Repeat("dup", 7));

        var result = EditBatch.Apply([Edit("dup", "uniq")], lines);

        Assert.Equal(
            "edit 1 of 1: oldStr found 7 times (lines 1, 2, 3, 4, 5, …) — add more context or set replaceAll: true",
            Assert.Single(result.Failures));
    }

    [Fact]
    public void Apply_ReplaceAllIsPerEdit_NotAFlagForTheWholeBatch()
    {
        // Every other fixture in this file carries ONE edit, so a batch-wide flag reintroduced in
        // `Apply` (`edits.Any(e => e.ReplaceAll is true)`) would be indistinguishable from the
        // per-edit one — the exact switch the schema cannot express and the model cannot reach.
        var lines = new List<string> { "aaa", "bbb", "aaa", "bbb" };
        var edits = new List<DiffEdit> { Edit("aaa", "X", replaceAll: true), Edit("bbb", "Y") };

        var result = EditBatch.Apply(edits, lines);

        // Edit 1 replaces both `aaa`; edit 2 finds two `bbb` and, without the flag, must REFUSE.
        Assert.Equal(1, result.AppliedEdits);
        Assert.Equal(2, result.TotalReplacements);
        Assert.Equal("edit 2 of 2: oldStr found 2 times (lines 2, 4) — add more context or set replaceAll: true",
            Assert.Single(result.Failures));
        Assert.Equal(new[] { "X", "bbb", "X", "bbb" }, lines);
    }

    [Fact]
    public void Apply_NullEditInTheArray_IsAPerEditFailure_NotACrash()
    {
        // `"edits": [null]` is legal JSON and deserialises to a null ELEMENT. The ordering pass
        // dereferences `ApproximateLine`, so this used to escape as a NullReferenceException — the
        // model got "Object reference not set to an instance of an object." instead of a sentence
        // naming the edit it has to fix.
        var lines = new List<string> { "one" };
        var edits = new List<DiffEdit> { null!, Edit("one", "ONE") };

        var result = EditBatch.Apply(edits, lines);

        Assert.Equal(1, result.AppliedEdits);
        Assert.Equal("edit 1 of 2: the edit is null. Each edit needs 'oldStr' and 'newStr' strings.",
            Assert.Single(result.Failures));
        Assert.Equal(new[] { "ONE" }, lines);
    }
}
