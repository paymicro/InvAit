# Edit matching (`edit_file` / `edits`)

How a model-authored `oldStr` / `newStr` becomes an actual file change, and which invariants of
that path must not be broken. Code: `ToolCore/EditApplier.cs` (VS host), used by
`InvAit/Agent/ToolExecutor.EditFileAsync`; TS mirror `VSCode/src/toolHandlers/diffMatch.ts`
(VS Code host).

## The pipeline

```
oldStr / newStr            file on disk
      │                         │
SplitEditText            FileUtils.ReadFileWithMetadataAsync
      │                         │
 List string  ────────►  List string   (both are lines WITHOUT any line ending)
      │                         │
      └────► EditApplier.ApplyEdit  ──►  FileUtils.SaveFileAsync
```

The applier never sees a line ending: it takes and returns a list of lines. Everything about
line endings is decided at the two boundaries — the reader and `SplitEditText` — plus, in the
VS Code host only, a third participant: the writer, which currently loses the final line break
(see [Known divergence](#known-divergence-the-vs-code-port)).

## `SplitEditText` — the single normalisation point

`EditApplier.SplitEditText` is the ONLY place an edit text becomes a line list. Both hosts were
doing this inline (`Regex.Replace(oldStr, "\r", "").Split('\n')` in C#,
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
END, middle lines must match exactly; a single-line search matches as a substring; empty search
lines match empty/whitespace-only target lines. A hint restricts the preferred range to
`±5` around `approximateLine`, with a fallback to the first match anywhere; without a hint an
exact whole-line block wins over an earlier fragment match.

Edits are applied bottom-to-top (descending `approximateLine`) so earlier edits cannot shift the
lines a later edit is looking for, and every edit is reported by its index in the MODEL's request,
not in the sorted copy.

A `null` `oldStr` (possible despite the non-nullable contract — `"oldStr": null` in JSON) is
rejected per edit as `oldStr is missing`. It must NOT reach the matcher: `SplitEditText(null)`
returns `[""]`, which would silently insert the replacement at the FIRST empty line of the file.
An explicitly empty `oldStr` stays legal — that is the legacy "matches an empty file line"
semantics.

## Replacement

A fully matched line is replaced verbatim (the replacement keeps its own indentation). A truncated
boundary line loses/receives only the matched fragment, keeping the rest of the line; a missing
replacement line deletes the matched line; extra replacement lines are inserted after the last
matched line.

## `FindInFile` is internal

`FindInFile` exists only for `ToolCore.Tests` (pinned via `InternalsVisibleTo`): it exposes the
matcher without the mutation so hint/tolerance/partial-boundary rules can be asserted directly.
The production entry point is `ApplyEdit`. Do not make it public again — a public member used by
tests only is an API surface nobody maintains.

## Known divergence: the VS Code port

`VSCode/src/toolHandlers/diffMatch.ts` is a partial port: it has `isMatch`/`findInFile` only, with
NO partial boundary-line matching (truncated first/last line), no exact-block preference and no
substring matching for a single line. Consequence: an edit the VS host accepts can fail in the
VS Code host. The split (and the trailing-break rule) is mirrored as `splitEditText` and is
verified equivalent to the C# version — but the matching itself is not yet in sync, and the
fileOps writer diverges in three more ways:

| Aspect | VS host (`FileUtils`) | VS Code host (`fileOps.ts`) |
|--------|----------------------|------------------------------|
| Final line break of the file | read into `HasFinalNewLine`, written back | **lost**: `pop()` drops the flag, `lines.join(lineEnding)` does not restore it |
| Separator of a CR-only file | `"\r"` | `"\n"` — the file is silently rewritten to LF |
| Encoding / BOM | preserved | hard-coded `utf-8`, BOM lost |

The final-newline loss is a pre-existing defect of the writer, not of `splitEditText`, but the
split fix makes it *reachable*: an `oldStr` ending with `\n` used to fail loudly, so the file was
not rewritten at all; now the edit succeeds and the diff shows `\ No newline at end of file`.
Bring `diffMatch.ts` to parity and give the TS writer a `hasFinalNewLine` flag before claiming
identical behaviour in both hosts.

## Tests

| What | Where |
|------|-------|
| Matching rules (hint, tolerance, partial boundaries, empty lines) | `ToolCore.Tests/EditApplierTests.cs`, `FindInFile_*` |
| Replacement semantics | `ToolCore.Tests/EditApplierTests.cs`, `ApplyEdit_*` |
| Normalisation: CRLF/LF/CR, trailing break, mixed file + mixed search end-to-end | `ToolCore.Tests/EditApplierTests.cs`, `SplitEditText_*` and `ApplyEdit_MixedEndingFileAndMixedEndingSearch_Applies` |
| The pinned behaviour-change and the phantom-empty-line guard | `ToolCore.Tests/EditApplierTests.cs`: `ApplyEdit_SearchEndingWithNewLine_FragmentPrefix_ReplacesWholeLine`, `ApplyEdit_PhantomEmptySearchLine_DoesNotMatch` |
| File with / without a final line break, round-tripped through `FileUtils` | `ToolCore.Tests/EditApplierTests.cs`: `ApplyEdit_FileWithFinalNewLine_KeepsIt` |
| TS split parity and non-string input | `VSCode/src/test/toolHandlers/fileOps.test.ts`, `splitEditText` |
