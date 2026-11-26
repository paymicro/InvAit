using Shared.Contracts;

namespace ToolCore;

/// <summary>
/// Outcome of one <c>edit_file</c> batch: what was applied, what was refused, and the exact text the
/// model reads back. The orchestration lives here rather than in the host, where none of it could be
/// tested.
/// </summary>
public sealed class EditBatchResult
{
    internal EditBatchResult(int appliedEdits, int totalReplacements, int totalEdits, List<string> failures)
    {
        AppliedEdits = appliedEdits;
        TotalReplacements = totalReplacements;
        TotalEdits = totalEdits;
        Failures = failures.AsReadOnly();
    }

    /// <summary>Edits whose outcome was <see cref="EditOutcome.Applied"/> — never a refused one.</summary>
    public int AppliedEdits { get; }

    /// <summary>How many blocks were replaced in total (<c>replaceAll</c> counts each occurrence).</summary>
    public int TotalReplacements { get; }

    /// <summary>How many edits the model sent, failures included.</summary>
    public int TotalEdits { get; }

    /// <summary>One entry per refused edit, in the order the edits were attempted.</summary>
    public IReadOnlyList<string> Failures { get; }

    /// <summary>
    /// Whether anything may be written. The refusal branch of <c>edit_file</c> hangs on this:
    /// a write with zero applied edits would rewrite the file for nothing and then report success.
    /// </summary>
    public bool AnyApplied => AppliedEdits > 0;

    /// <summary>
    /// The model-facing name of the file, supplied by the CALLER: the batch works on an in-memory line
    /// list and does not know where those lines came from.
    /// </summary>
    public string File { get; set; } = string.Empty;

    /// <summary>
    /// The one place the model-facing sentences of <c>edit_file</c> are built. Three shapes, and
    /// the caller must send exactly one of them:
    /// <list type="bullet">
    /// <item><b>dryRun</b> — <c>Dry run on {file}: {n}/{m} edits would apply, {k} replacements.</c></item>
    /// <item><b>nothing applied</b> — <c>No valid replacements in {file}.</c> (a refusal: the caller
    /// must not write the file)</item>
    /// <item><b>some applied</b> — <c>Changes successfully applied to {file}. {n}/{m} edits, {k} replacements.</c></item>
    /// </list>
    /// Each of them is followed by the per-edit failure list; the model must see WHICH edit failed
    /// and why, so it can re-send only the failed ones instead of the whole batch.
    /// </summary>
    /// <param name="dryRun">Format the would-change sentence instead of the written one</param>
    /// <param name="normalisedSeparator">The file's separator when the read reported mixed line
    /// endings and the write normalised them; null when there is nothing to report</param>
    public string FormatReport(bool dryRun, string? normalisedSeparator = null)
    {
        // Zero failures is an empty string, not a "Failed edits:" heading with nothing under it.
        var report = Failures.Count == 0
            ? string.Empty
            : "\nFailed edits:\n - " + string.Join("\n - ", Failures);

        // Built here, not in the host: a sentence built in two hosts eventually differs in two hosts.
        // It must not claim the rewrite was localised — the write re-joins EVERY line with the chosen
        // separator, so untouched lines change ending too. The leading break keeps it off the last
        // failure bullet.
        var note = dryRun || normalisedSeparator == null
            ? string.Empty
            : $"\nNote: the file had mixed line endings; it was rewritten with '{TextFileCodec.SeparatorName(normalisedSeparator)}' throughout.";

        if (dryRun)
        {
            return $"Dry run on {File}: {AppliedEdits}/{TotalEdits} edits would apply, " +
                   $"{TotalReplacements} replacements.{report}";
        }

        if (!AnyApplied)
        {
            return $"No valid replacements in {File}.{report}";
        }

        return $"Changes successfully applied to {File}. " +
               $"{AppliedEdits}/{TotalEdits} edits, {TotalReplacements} replacements.{report}{note}";
    }

}

/// <summary>
/// The whole in-memory orchestration of <c>edit_file</c>: validation, ordering, application and
/// counting. Reads and writes belong to the caller — this type only ever sees a line list, so it
/// is testable without a solution, a file system or Visual Studio.
/// </summary>
public static class EditBatch
{
    /// <summary>
    /// Validates, orders (approximateLine DESC, original index preserved), applies each edit
    /// through <see cref="EditApplier.ApplyEdit"/>, accumulates per-edit failures, and returns the
    /// counts. <paramref name="lines"/> is mutated in place, bottom-to-top.
    /// </summary>
    /// <param name="edits">The batch exactly as the model sent it — order and indices matter for the report</param>
    /// <param name="lines">File lines; mutated in place by the applied edits</param>
    /// <param name="tolerance">Offset tolerance around <see cref="DiffEdit.ApproximateLine"/></param>
    /// <remarks>
    /// There is no batch-wide <c>replaceAll</c>: <see cref="DiffEdit.ReplaceAll"/> is the per-edit form
    /// the schema declares.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">An <see cref="EditOutcome"/> this switch does
    /// not classify. Thrown instead of counted as applied — see the remark in the loop.</exception>
    public static EditBatchResult Apply(List<DiffEdit> edits, List<string> lines, int tolerance = 5)
    {
        if (edits == null)
            throw new ArgumentNullException(nameof(edits));
        if (lines == null)
            throw new ArgumentNullException(nameof(lines));

        var totalReplacements = 0;
        var appliedEdits = 0;
        var failures = new List<string>();

        // The model re-sends only the failed edits, by their number in ITS request, so the original
        // index is carried through the sort. A JSON `null` element is legal and stays null: the `?.`
        // below keeps one bad element from becoming a null-reference fault instead of a sentence.
        var orderedEdits = edits
            .Select((rep, i) => (Rep: rep, Index: i))
            .OrderByDescending(x => x.Rep?.ApproximateLine)
            .ToList();

        foreach (var (rep, index) in orderedEdits)
        {
            if (rep is null)
            {
                failures.Add($"edit {index + 1} of {edits.Count}: the edit is null. " +
                             "Each edit needs 'oldStr' and 'newStr' strings.");
                continue;
            }

            var label = EditLabel(index, edits.Count, rep);

            // SplitEditText(null) would return [""] and insert newStr into the FIRST blank line. An
            // EMPTY string stays legal: it means "match a blank line".
            if (rep.OldStr is null)
            {
                failures.Add($"{label}: oldStr is missing");
                continue;
            }

            // The same trap from the other side: a null newStr would replace the block with ONE EMPTY
            // LINE and report "applied". `""` stays legal — it clears the block explicitly.
            if (rep.NewStr is null)
            {
                failures.Add($"{label}: newStr is missing. Send the full replacement text; " +
                             "an empty string leaves an empty line, and a lone line cannot be deleted here " +
                             "(use create_file to rewrite the file).");
                continue;
            }

            // SplitEditText is the single normalisation point, so an oldStr ending with \n does not
            // become a phantom empty search line.
            var search = EditApplier.SplitEditText(rep.OldStr);
            var replace = EditApplier.SplitEditText(rep.NewStr);
            var hint = rep.ApproximateLine ?? -1;
            // ReplaceAll is `bool?` because the schema allows null, which means false.
            var outcome = EditApplier.ApplyEdit(lines, search, replace, hint, tolerance, rep.ReplaceAll is true);

            // Every outcome is named EXPLICITLY and `default` THROWS. A default that counted as
            // applied would let a refused edit pass the write gate and report success.
            switch (outcome.Outcome)
            {
                case EditOutcome.Applied:
                    appliedEdits++;
                    totalReplacements += outcome.AppliedCount;
                    break;

                case EditOutcome.NotFound:
                    failures.Add($"{label}: oldStr not found");
                    break;

                // Refused instead of guessing: the model sees the lines and can add context or send
                // replaceAll.
                case EditOutcome.Ambiguous:
                    failures.Add($"{label}: oldStr found {outcome.MatchCount} times " +
                                 $"({DescribeLines(outcome.MatchLines)}) — add more context or set replaceAll: true");
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(outcome),
                        outcome.Outcome,
                        "EditOutcome is not classified as applied or failed — add a case instead of letting it count as applied.");
            }
        }

        return new EditBatchResult(appliedEdits, totalReplacements, edits.Count, failures);
    }

    /// <summary>
    /// At most five 1-based line numbers, so the model sees where to look.
    /// </summary>
    internal static string DescribeLines(IReadOnlyList<int> matchLines)
    {
        const int shown = 5;
        var first = string.Join(", ", matchLines.Take(shown).Select(line => line + 1));
        return matchLines.Count > shown ? $"lines {first}, …" : $"lines {first}";
    }

    /// <summary>
    /// The label of a failed edit — "edit 2 of 3 (line 42)". The "(line …)" part is printed ONLY when
    /// the model sent a hint; with null it used to read "line -1".
    /// </summary>
    internal static string EditLabel(int index, int total, DiffEdit rep)
    {
        var line = rep.ApproximateLine is null ? string.Empty : $" (line {rep.ApproximateLine})";
        return $"edit {index + 1} of {total}{line}";
    }
}
