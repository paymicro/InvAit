using Shared.Contracts;

namespace ToolCore;

/// <summary>
/// Outcome of one <c>edit_file</c> batch: what was applied, what was refused, and the exact
/// text the model reads back.
///
/// This type exists because the batch orchestration used to live in the VS host, where it could
/// not be tested: the counting, the ordering and — most importantly — the model-facing sentences
/// were only reachable by hand through a WebView2 round trip. The host now parses, reads, calls
/// <see cref="EditBatch.Apply"/> and writes; every number and every sentence below is covered by
/// <c>ToolCore.Tests</c>.
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
    /// The model-facing name of the file, supplied by the CALLER right after
    /// <see cref="EditBatch.Apply"/> — ToolCore never sees or resolves a path, and the model must be
    /// told which file a batch touched. Kept out of <see cref="EditBatch.Apply"/> on purpose: the
    /// batch works on an in-memory line list and does not know where those lines came from.
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
        // Per-edit отчёт: пустой oldStr матчит пустую строку файла, поэтому ноль отказов — это
        // пустая строка, а не «Failed edits:» без единой строки под ним.
        var report = Failures.Count == 0
            ? string.Empty
            : "\nFailed edits:\n - " + string.Join("\n - ", Failures);

        // The normalisation note belongs HERE, not in the host: it is a model-facing sentence, and a
        // sentence built in two hosts is a sentence that will eventually differ in two hosts.
        // The note must not localise the rewrite: `SaveFileAsync` re-joins EVERY line with the
        // chosen separator, so untouched lines change ending too. Saying "where the edit touched it"
        // promised a precision the write does not have.
        // It also starts with a line break of its own: the failure list above ends without one, so
        // a leading space glued two unrelated statements into the last failure bullet.
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
    /// There is no batch-wide <c>replaceAll</c>: it used to exist as a parameter and the VS host read
    /// it from a payload key that is NOT in the tool schema, so it was false on every real call and
    /// only the tests could turn it on — a switch the model could not reach. <see cref="DiffEdit.ReplaceAll"/>
    /// is the per-edit form the schema actually declares.
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

        // Оригинальные индексы: модель повторяет только упавшие правки по номеру в своём
        // запросе. Индекс здесь 0-based, но в отчёт идёт 1-based (см. EditLabel).
        // A JSON `null` inside the array is legal and deserialises to a null element. Refused per
        // edit, not thrown: the sort dereferences `ApproximateLine`, so without the `?.` below this
        // was a NullReferenceException out of the batch instead of a sentence the model can act on.
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

            // OldStr в контракте non-nullable, но JSON с "oldStr": null всё равно даёт null.
            // Явный отказ: SplitEditText(null) вернул бы [""] и правка молча вставила бы
            // newStr в ПЕРВУЮ пустую строку файла. Пустая строка остаётся легальной
            // (легаси-семантика «oldStr матчит пустую строку файла»).
            if (rep.OldStr is null)
            {
                failures.Add($"{label}: oldStr is missing");
                continue;
            }

            // Та же ловушка с другой стороны: SplitEditText(null) == [""], то есть обрезанный
            // newStr молча заменил бы найденный блок ОДНОЙ ПУСТОЙ СТРОКОЙ и отчитался «applied».
            // Пустая строка "" остаётся легальной (явно очищает блок), null — нет.
            if (rep.NewStr is null)
            {
                failures.Add($"{label}: newStr is missing. Send the full replacement text; " +
                             "an empty string leaves an empty line, and a lone line cannot be deleted here " +
                             "(use create_file to rewrite the file).");
                continue;
            }

            // SplitEditText — the single normalisation point: mixed line endings (CRLF/LF/CR)
            // and one trailing line break treated as a terminator, so an oldStr ending with \n
            // does not turn into a phantom empty search line.
            var search = EditApplier.SplitEditText(rep.OldStr);
            var replace = EditApplier.SplitEditText(rep.NewStr);
            var hint = rep.ApproximateLine ?? -1;
            // ApplyEdit replaces only the matched FRAGMENT of partially matched lines
            // (e.g. search "List<string" inside "var fileContent = new List<string>"),
            // and whole lines for exact matches.
            // ReplaceAll — bool?, потому что схема разрешает null: null значит false.
            var outcome = EditApplier.ApplyEdit(lines, search, replace, hint, tolerance, rep.ReplaceAll is true);

            // Every outcome is named EXPLICITLY and `default` throws: this switch used to be
            // `NotFound`/`Ambiguous`/`default: appliedEdits++`, so the day a third failure member
            // was added to EditOutcome a REFUSED edit was counted as applied — the write gate saw
            // `appliedEdits != 0`, the file was written unchanged and the model was told
            // "Changes successfully applied". A new outcome must be classified here, not absorbed.
            switch (outcome.Outcome)
            {
                case EditOutcome.Applied:
                    appliedEdits++;
                    totalReplacements += outcome.AppliedCount;
                    break;

                case EditOutcome.NotFound:
                    failures.Add($"{label}: oldStr not found");
                    break;

                // Отказ вместо угадывания: молчаливая замена ПЕРВОГО вхождения — это правка
                // не того участка без предупреждения. Модель видит линии и может уточнить контекст
                // или прислать replaceAll.
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
    /// Короткий список найденных линий для отчёта об неоднозначности: максимум пять 1-based
    /// номеров, чтобы модель видела ГДЕ искать и могла прислать уточняющий контекст.
    /// </summary>
    internal static string DescribeLines(IReadOnlyList<int> matchLines)
    {
        const int shown = 5;
        var first = string.Join(", ", matchLines.Take(shown).Select(line => line + 1));
        return matchLines.Count > shown ? $"lines {first}, …" : $"lines {first}";
    }

    /// <summary>
    /// Подпись упавшей правки для отчёта: «edit 2 of 3 (line 42)». Индекс — 1-based, как и
    /// номера линий в том же предложении («of N» снимает вопрос, что это за число), а часть
    /// «(line …)» печатается ТОЛЬКО при присланной модели подсказке: иначе в отчёт попадало
    /// «line -1», потому что ApproximateLine == null превращался в -1.
    /// </summary>
    internal static string EditLabel(int index, int total, DiffEdit rep)
    {
        var line = rep.ApproximateLine is null ? string.Empty : $" (line {rep.ApproximateLine})";
        return $"edit {index + 1} of {total}{line}";
    }
}
