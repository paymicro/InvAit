# Edit matching (`edit_file` / `edits`)

How a model-authored `oldStr` / `newStr` becomes an actual file change, and which invariants of
that path must not be broken.

Code, VS host: `ToolCore/EditApplier.cs` (the matcher), `ToolCore/EditBatch.cs` (the batch:
per-edit validation, ordering, counters and every model-facing sentence) and `ToolCore/TextLines.cs`
(the ONE splitter). `InvAit/Agent/ToolExecutor.EditFileAsync` only parses, resolves, checks, reads,
calls `EditBatch.Apply` and saves — see
[Why the batch is not in the host](#the-batch-lives-in-toolcore).
TS mirror: `VSCode/src/toolHandlers/diffMatch.ts`.

## The pipeline

```
oldStr / newStr            file on disk
      │                         │
SplitEditText → TextLines   FileUtils.ReadFileWithMetadataAsync
      │                         │
 List string  ────────►  List string   (both are lines WITHOUT any line ending)
      │                         │
      └────► EditApplier.ApplyEdit  ──►  FileUtils.SaveFileAsync
```

The applier never sees a line ending: it takes and returns a list of lines. Everything about line
endings is decided at the two boundaries — the reader and `SplitEditText` on the way in, and the
writer that has to put them back on the way out.

## `SplitEditText` — the single normalisation point

`EditApplier.SplitEditText` is now a ONE-LINE delegate to `TextLines.SplitEditText`, and the splitter
exists ONCE: `TextLines.FromText` is the only code that turns a text into a line list, for the file
reader and for the edit text alike. Both hosts used to do this inline
(`Regex.Replace(oldStr, "\r", "").Split('\n')` in C#,
`replace(/\r/g,'').split('\n')` in TS), and both had the same two defects, which is why the
normalisation moved into a tested function:

1. **One trailing line break is a terminator.** `"line2\n"` used to split into `["line2", ""]` —
   a two-line block whose second line demanded a real empty line. On any file where the next line
   had content the edit failed with `oldStr not found`, even though the line was right there. This
   was the reported "sometimes does not find a line ending with `\n`"; it hits the most common
   single-line edit, because models routinely copy `oldStr` out of a numbered listing together
   with its trailing newline. A SECOND trailing break is kept — there the model really did address
   a trailing empty line.
2. **Mixed endings must meet.** A file may mix CRLF/LF/CR, and the search block may carry a
   different mix. `FileUtils` already splits on `["\r\n", "\r", "\n"]`, so the only remaining job
   is to normalise the *search* text the same way before splitting; otherwise a stray `\r` stays
   inside a search line (it survives `Trim` comparisons by luck, but not `StartsWith`/`EndsWith`
   partial matching on the raw target line).

Empty or missing text yields a single empty line — an empty `oldStr` addresses an empty file line
(legacy semantics, pinned by `SplitEditText_EmptyText_ReturnsSingleEmptyLine`).

## Matching rules (authoritative: `EditApplier` header)

Case-insensitive; search lines are trimmed (their own indentation is ignored); search lines must
occupy consecutive target lines. First block line may be truncated at the START, last line at the
END, middle lines must match exactly; empty search lines match empty/whitespace-only target lines.
A single-line search is a SUBSTRING match: at column 0 or in the middle of an indented line only the
matched part is replaced, so the rest of the line — including a trailing `// comment` — survives.
An all-empty search block matches N consecutive empty lines and is never ambiguous. It is settled
BEFORE `replaceAll` is looked at, because an empty search is not a pattern: `oldStr: "\n"` is a very
natural spelling of "a blank line" and matches every blank line in the file, so letting `replaceAll`
have it changed the scope from one line to the whole file.

`approximateLine` is a *preference*, not a filter: the ±5 window around it is searched first, and if
that window holds no candidate the search falls back to the WHOLE candidate set. It does not fall back
to the first match anywhere — several candidates in that set are refused (see below), so a bad hint
costs an extra refusal, never a wrong line. Without a hint an exact whole-line block wins over an
earlier fragment match.

Edits are applied bottom-to-top (descending `approximateLine`) so earlier edits cannot shift the
lines a later edit is looking for, and every edit is reported by its index in the MODEL's request,
not in the sorted copy.

A file the matcher cannot trust is refused before any search: above 2 MB, or binary, the edit is not
attempted and the answer is `edit NOT attempted on <file>: <reason>`. That wording is deliberate —
reporting `oldStr not found` would send the model off rewriting a search block that was never
compared against anything.

A `null` `oldStr` (possible despite the non-nullable contract — `"oldStr": null` in JSON) is
rejected per edit as `oldStr is missing`. It must NOT reach the matcher: `SplitEditText(null)`
returns `[""]`, which would silently insert the replacement at the FIRST empty line of the file.
An explicitly empty `oldStr` stays legal — that is the legacy "matches an empty file line"
semantics. `newStr: null` is refused by name too: a null replacement is a malformed request, not an
instruction to delete.

## Replacement

A fully matched line is replaced verbatim (the replacement keeps its own indentation). A truncated
boundary line loses/receives only the matched fragment, keeping the rest of the line; a missing
replacement line deletes the matched line; extra replacement lines are inserted after the last
matched line.

One thing the tool cannot do is delete a *lone* line: `SplitEditText("")` is `[""]`, so an empty
`newStr` rewrites the matched line to an empty one instead of removing it — "one empty line" and
"no line" are the same payload. Removing a line means rewriting the file with `create_file`.

## Ambiguity is a refusal (`replaceAll`)

One edit must match exactly one place. When `oldStr` occurs several times, the edit is NOT
applied and the tool response lists the count and the matching lines (1-based, the numbers you see in
the transcript):

```
edit 2 of 3 (line 10): oldStr found 4 times (lines 10, 24, 31, 52) — add more context or set replaceAll: true
```

The label is `edit {i} of {n}`, 1-based like the line numbers next to it, and `(line …)` appears only
when the model actually sent `approximateLine` — otherwise the report used to print `line -1`. At most
five line numbers are listed; the rest are elided with `…`, so the model still knows WHERE to look
without the payload growing with the file.

Silently replacing the first occurrence is how an edit lands in the wrong method: the model
gets a success message, the file is changed, and nobody notices until the tests fail. The
previous behaviour did exactly that.

The choice is made by `EditApplier.SelectSingle`, and the tie-breakers run in this order:

1. an all-empty search block is settled FIRST, before any counting AND before `replaceAll`: it
carries no text to disambiguate with, so the legacy rule stands — a blank line near the hint
(the nearest one inside it), otherwise the first empty line in the file. This is a SAFETY VALVE,
not a preference: no amount of added context can make an empty search unique, so refusing it would
hand the model an edit it can never repair, and handing it to `replaceAll` would rewrite every blank
line in the file;
2. otherwise a single occurrence wins;
3. the hint window (`approximateLine` ± 5) narrows the set of candidates;
4. among the candidates an exact whole-line block beats a fragment (substring) hit;
5. anything left is ambiguous.

Consequence worth knowing: with the 5-line tolerance, a hint cannot disambiguate duplicates that
sit closer than ~10 lines to each other. That is intended — the hint is a position hint, not an
identity.

`replaceAll: true` on an edit replaces EVERY occurrence, bottom-to-top (so an applied replacement
cannot shift a pending index), skipping overlapping candidates. The response counts
replacements separately from edits: `3/4 edits, 7 replacements`. An `approximateLine` hint is IGNORED
by this branch — `replaceAll` means all of them, file-wide — and the payload does not say so: the
schema describes the flag as "replace every occurrence", which a reader can take as "every occurrence
of the hinted region".

The flag never decides the scope of an EMPTY search: an all-empty block is settled before the branch
runs, in BOTH hosts (`EditApplier.ApplyEdit` and `applyEdit`), so `oldStr: "\n"` + `replaceAll: true`
touches one blank line and not every one. `selectEditTarget` has no such guard of its own — it reaches
the same rule through `selectSingle`, which the C# `SelectSingle` does not need because its only
caller guards first.

An empty search is NEVER ambiguous, so its hint is NOT resolved by the "is the window unambiguous"
question: with two blank lines inside `approximateLine +/- 5` that question answers "no", and taking
the first candidate in the FILE instead would put the edit near the top of a long file.

Within the window the answer is the candidate NEAREST the hint, not the first one in it — and only
with no candidate in the window does it fall back to the first blank line in the file
(`EditApplier.FirstEmptyLine` / `diffMatch.firstEmptyLine`). "First in the window" is wrong because
the window is five lines wide in each direction: a call that says "put the section header here" with
a hint on line 33 and blank lines at 30 and 32 landed on **30**, touched nothing the model pointed
at, and still answered `1 replacements` — a silent miss with no signal to recover from. A tie keeps
the earlier line, and that tie rule is what makes the two hosts agree: it is about determinism, not
about the distance rule, and the tests say so.

When writing a test for this, check that BOTH blank lines fall inside the window. With one candidate
inside it, "first" and "nearest" are the same answer and the test passes against either
implementation — the first version of this test did exactly that and proved nothing.

`EditApplyResult` carries `Outcome` (`Applied` / `NotFound` / `Ambiguous`), `StartLine` (0-based,
-1 when nothing was applied), `AppliedCount` and `MatchLines`, so a caller never has to decode a
`-1`.

## The batch lives in `ToolCore`

`ToolCore/EditBatch.cs` (public) owns the whole in-memory orchestration of `edit_file`: per-edit
validation, the ordering, the `EditOutcome` switch, the counters, and the strings the model reads
back. `EditBatchResult` exposes `AppliedEdits`, `TotalReplacements`, `TotalEdits`, `Failures`,
`AnyApplied`, `File` and `FormatReport(bool dryRun, string? normalisedSeparator = null)`; the three
shapes of that report are the dry-run sentence, the "nothing applied" refusal and the success
sentence, each followed by the per-edit failure list. The mixed-line-ending note is built HERE too —
the caller passes the separator the read reported and `FormatReport` decides whether there is
anything to say:

```
Note: the file had mixed line endings; it was rewritten with '{X}' throughout.
```

The sentence starts its own line and does **not** say "where the edit touched it". The write re-joins
EVERY line with the chosen separator, so untouched lines change ending too — the earlier wording
promised a precision the write does not have, and it merged into the last failure bullet because the
failure list does not end with a line break. No model-facing sentence is assembled in a host: a
sentence built twice eventually differs twice. The `crlf`/`cr`/`lf` mapping has ONE owner,
`TextFileCodec.SeparatorName`, which `FormatReport` calls directly.

There is **no batch-wide `replaceAll`**. It existed as an `EditBatch.Apply` parameter and the VS host
read it from a payload key the tool schema never declared, so it was false on every real call and only
`EditBatchTests` could turn it on — a switch the model could not reach. `DiffEdit.ReplaceAll` (per
edit, `bool?`, `null` = false) is the form the schema actually declares.

Because the flag is per edit, the UI badges it as a **ratio over the edits the call carries**
(`replaceAll 1/2 edits`), not as a flag and not as a replacement count. A boolean flag would read as
"the whole batch replaces everything", and the earlier `×N` label was worse than imprecise — it was
misleading: next to a result that said `1/2 edits, 4 replacements` a `×1` badge reads as "one
replacement". The replacement count **cannot** appear in the badge at all: it is a property of the
FILE, and the host learns it only by running the call; the badge renders from the arguments. The
per-edit detail stays in the tool arguments the badge summarises — no per-edit marker is rendered.
The badge appears only when at least one received edit carries the flag (unlike the `create_file`
mode badge, which always renders), and it counts only the edits already received, so a partially
streamed batch shows a partial ratio.

The count is computed in `DiffView.OnParametersSet` next to the diff parsing — not in a computed
getter — because the header re-renders on every streamed args chunk. It is assigned OUTSIDE the
re-parse gate: flipping `replaceAll` on an already-parsed edit leaves the character sum and the edit
count identical, so a gate-guarded assignment would leave a stale badge. `dryRun` gets its own badge
for the same reason — a successful-looking diff is otherwise indistinguishable from a real write.
That badge is a WARNING, not informational: it reports that NOTHING was written, and "info" reads as
a neutral remark about a completed action — the exact misreading it exists to prevent. Both styles are
pinned by tests, so neither can regress silently.

Two things there are load-bearing:

- **Ordering** is descending `approximateLine` so a later edit cannot shift the lines an earlier one
  is looking for — while the report still numbers every edit by its index in the MODEL's request, not
  in the sorted copy. A model that re-sends "edit 2" must be talking about the same edit it sent.
- **The outcome switch has no absorbing `default:`.** `NotFound` and `Ambiguous` are classified, and
  `default:` throws `ArgumentOutOfRangeException`. It used to be `NotFound` / `Ambiguous` /
  `default: appliedEdits++`, so the day a new `EditOutcome` member appeared a REFUSED edit would have
  been counted as applied, the write gate (`AnyApplied`) would have opened, the file would have been
  rewritten unchanged and the model would have read "Changes successfully applied". A new outcome must
  be classified here, not absorbed.

### Why the batch is not in the host

`InvAit` targets .NET Framework 4.8 against the VS SDK and has no usable test harness, so it must
stay logic-free: anything that can be wrong belongs in `ToolCore`, where `ToolCore.Tests` covers it.
`ToolExecutor.EditFileAsync` followed that rule and shrank from ~180 lines to ~91 — it now only
parses, resolves, checks, reads, calls `EditBatch.Apply` and saves. Before, the counting, the ordering
and every model-facing sentence were reachable only by hand through a WebView2 round trip.

`InvAit.Tests/InvAit.Tests.csproj` exists as an EMPTY scaffold — zero `.cs` files, not listed in
`InvAit.slnx` — and is NOT the guard. The guard is `ToolCore.Tests` + `UIBlazor.Tests` + the VS Code
mocha suite; anything the host keeps for itself is untested by construction.

## Schema and `dryRun`

`edit_file` obeys the same strict-schema rule as every other tool: optionality is expressed only by
the null union, so the top-level object AND each nested `edits` entry list EVERY property in
`required`, and a call that wants a default sends `null` rather than omitting the key. `dryRun` is
optional in exactly that sense: `true` computes the edit in memory and writes nothing.

## The tests are still named `FindInFile_*`

There is NO `FindInFile` method any more. It was an `internal` single-match view of the matcher
(`FindAllMatches` + `SelectSingle`) kept for `ToolCore.Tests`, and it returned `-1` for BOTH "not
found" and "ambiguous" — the two answers a caller must never be able to confuse. Production has
always gone through `ApplyEdit` and read `EditApplyResult.Outcome`.

Consequence for the next reader: `EditApplierTests` still carries ~44 test methods whose NAMES start
with `FindInFile_` (`FindInFile_ExactMatch_ReturnsCorrectIndex`, `FindInFile_NoMatch_ReturnsMinusOne`,
…), and they now call `EditApplier.ApplyEdit(...)` and assert on `.StartLine` / `.Outcome`. The names
say which rules the cases pin (hint, tolerance, partial boundaries, empty lines), not which method
they enter through. Do not go looking for the method — and do not re-add it: a public member used by
tests only is an API surface nobody maintains, and a helper that loses the difference between "absent"
and "ambiguous" is the bug, not the shortcut.

## The VS Code port

`VSCode/src/toolHandlers/diffMatch.ts` is a FULL port of the C# matcher, not the whole-line
comparison it once was: fragment suffix/prefix matching on the boundary lines, substring matching for
a single-line search, whole-line verbatim replacement, trailing-suffix attach and the
whole-line-over-fragment tie-breaker all exist there. The `findInFile` entry point that TS module
used to expose is gone — an interface that could not express "ambiguous" was the thing keeping the port
partial. (It was never the same thing as the C# `FindInFile` test helper described above, which is
gone too.)

### Known divergence

The divergences this section used to tabulate are gone: the TS split, the selection policy and the
matcher itself now agree with `EditApplier`, and the writer defects that used to be listed here — the
lost final line break, the CR-only file rewritten to LF, the hard-coded `utf-8` — sat on the same TS
path and were fixed with it.

What is still true is worth knowing at night: these are two independent implementations. No shared
code, no generated source — only two test suites that have to be extended together. A matching rule
added to `EditApplier` and not to `diffMatch.ts` yields an edit the VS host accepts and the VS Code
host refuses, or applies where you did not intend. When you touch either side, port against
[Matching rules](#matching-rules-authoritative-editapplier-header) and
[Replacement](#replacement), and extend both suites in the same change.

## Known limitations

Real gaps, not design choices; tracked in the local-only `todo.md` (not linked: it is untracked).

- **The dominant separator wins, not each line's own.** A file with mixed `\r\n` / `\n` is normalised
to its dominant separator when it is written back. The payload says so — reported, never silent —
but per-line terminators do not survive.
- **No workspace boundary.** `edit_file` accepts an absolute path anywhere on disk; nothing checks
that the file belongs to the open solution. `create_file` with `mode=append` widens that, because a
bad call adds to a file instead of replacing it.
- **No dirty-buffer check.** The tool writes to disk while the editor may hold unsaved changes for
the same file, and VS can later save its buffer over the edit. Save first.
- **No test for the VS host wiring.** `InvAit` (net48 + VS SDK) has no test harness, so only
  `ToolCore` is covered; `InvAit.Tests/` is an empty scaffold, not a guard — see
  [file-writes.md](file-writes.md#tests).

## Tests

| What | Where |
|------|-------|
| Matching rules (hint, tolerance, partial boundaries, empty lines) | `ToolCore.Tests/EditApplierTests.cs`, `FindInFile_*` |
| Replacement semantics | `ToolCore.Tests/EditApplierTests.cs`, `ApplyEdit_*` |
| Normalisation: CRLF/LF/CR, trailing break, mixed file + mixed search end-to-end | `ToolCore.Tests/EditApplierTests.cs`, `SplitEditText_*` and `ApplyEdit_MixedEndingFileAndMixedEndingSearch_Applies` |
| Batch orchestration: ordering by `approximateLine` with the model's indices, per-edit refusals, counters, the three report shapes | `ToolCore.Tests/EditBatchTests.cs` |
| Ambiguity refusal and `replaceAll` | `ToolCore.Tests/EditApplierTests.cs`, region `Ambiguity and replaceAll`; `VSCode/src/test/toolHandlers/fileOps.test.ts`, `editFiles ambiguity and replaceAll` |
| An all-empty search is settled before `replaceAll`, in BOTH hosts, and its hint wins over "first blank line of the file" | `ToolCore.Tests/EditApplierTests.cs`: `ApplyEdit_ReplaceAll_DoesNotTurnAnEmptySearchIntoEveryBlankLine`, `ApplyEdit_EmptySearchWithTwoBlankLinesInTheWindow_UsesTheHintedOne`; `VSCode/src/test/toolHandlers/diffMatch.test.ts`: the same two shapes under `applyEdit replacement` |
| `replaceAll` is PER EDIT (a batch-wide flag reddens it) | `ToolCore.Tests/EditBatchTests.cs`: `Apply_ReplaceAllIsPerEdit_NotAFlagForTheWholeBatch` |
| The pinned behaviour-change and the phantom-empty-line guard | `ToolCore.Tests/EditApplierTests.cs`: `ApplyEdit_SearchEndingWithNewLine_FragmentAtColumnZero_KeepsTrailingComment`, `ApplyEdit_SearchEndingWithNewLine_MatchesSingleLine`, `ApplyEdit_PhantomEmptySearchLine_DoesNotMatch` |
| File with / without a final line break, round-tripped through `FileUtils` | `ToolCore.Tests/EditApplierTests.cs`: `ApplyEdit_FileWithFinalNewLine_KeepsIt` |
| TS split parity and non-string input | `VSCode/src/test/toolHandlers/fileOps.test.ts`, `splitEditText` |
| The `replaceAll` / `dryRun` badges: per-edit count, `null` not counted, dry run only when asked | `UIBlazor.Tests/Components/ToolViews/DiffViewTests.cs`, region `Call-argument badges` |
| The badges receive the call's arguments, including a tolerant `dryRun` (`"yes"`, `1`) that must not blank the block | `UIBlazor.Tests/Components/ToolViews/ToolCallBlockTests.cs`, `EditFileTool_*` |
