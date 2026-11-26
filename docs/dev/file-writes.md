# File writes (`create_file`) and file identity

How an encoding and a line ending are decided: what `create_file` puts on disk, and how an existing
file is IDENTIFIED before either `create_file` or `edit_file` touches it. The two tools share one
identity on purpose — an edit re-saves the whole file, so a wrong decode there is not a display
problem, it is a rewrite.

Code, VS host: `ToolCore/FileWriter.cs` (the write path) on top of four single homes —
`ToolCore/FileIdentity.cs` (the BOM ladder, the two-encoding split), `ToolCore/TextFileCodec.cs`
(strict encode, the strict-UTF-8 probe, the ONE separator algorithm, the charset table, the EOL and
byte helpers), `ToolCore/TextLines.cs` (the ONE splitter) and `ToolCore/FileUtils.cs` (the read
path). `.editorconfig` resolution lives in `ToolCore/EditorConfig.cs`, whose
`EditorConfigSettings.CreateEncoding` delegates to `TextFileCodec.CreateEncoding`.

VS Code mirror: `VSCode/src/toolHandlers/fileWriter.ts` + `fileReader.ts` + `encoding.ts` +
`editorConfig.ts`. Everything encoding-shaped is `encoding.ts`, which imports NOTHING (no `fs`, no
`path`) — that is what makes it testable without stubs.

## The defect this replaces

The old implementation was one line — `File.WriteAllText(path, content, Encoding.UTF8)` in the VS
host and `fs.writeFileSync(path, content, 'utf-8')` in VS Code — and both were wrong in the same
way: they ignored the tree. In .NET `Encoding.UTF8` means **UTF-8 with a BOM**, so every file the
tool created in a `charset = utf-8` repository started with `EF BB BF`. That is invisible in the
editor and very visible everywhere else: git shows the first line changed, C# sees a stray U+FEFF
before the first token, JSON parsers reject the document. Twelve files in this repository still start
with a BOM (vendored `_content` assets restored from NuGet are not counted); the local-only `todo.md`
carries the list and the command that re-counts them, and is not linked here because it is untracked.

The Node side had the mirror-image problem: `'utf-8'` never writes a BOM, so a
`charset = utf-8-bom` tree got files that violate its own rule.

## Rules

| Situation | Encoding | Line endings |
|-----------|----------|--------------|
| New file, `.editorconfig` resolves `charset` | that charset | `end_of_line` if it resolves, else content verbatim |
| New file, no `.editorconfig` value | UTF-8 **without** BOM | content verbatim — no guessing at the platform newline |
| Existing file whose first bytes are a BOM | sniffed: `utf-8-bom`, `utf-16le`, `utf-16be`, `utf-32le`, `utf-32be` | the new text is normalised to the file's dominant separator |
| Existing file, no BOM, bytes are strict UTF-8 | `utf-8` | same |
| Existing file, no BOM, **not** strict UTF-8, no `charset` declared | **refused**, nothing is written | — |
| Existing file, no BOM, **not** strict UTF-8, `charset` declared | that charset, reported `(from .editorconfig)` | same |
| Existing file, `append` / `prepend` | the existing bytes are concatenated, never re-encoded | the glue is the file's dominant separator: for `append` added when the **existing bytes** end with no line break of any kind, for `prepend` when the **new text** does — prepending is the side that runs into the old first line |
| Existing file, `overwrite` | the existing bytes are **deleted**; the body is encoded fresh | the new text is normalised to the file's dominant separator; `.editorconfig` `end_of_line` only when the file has no break of its own |
| A character the resolved charset cannot represent | the write **fails** | — |
| Content starts with `U+FEFF` | the leading `U+FEFF` is stripped, **reported** — the BOM is the writer's, never the content's | — |

Why "verbatim" for a new file with no rule: rewriting `\n` into `\r\n` because the host is Windows
is a change nobody asked for, and it is indistinguishable from a bug when the caller built the
content byte-exactly.

Why an existing file is *identified* before it is touched: re-encoding a file is a semantic change —
its bytes, and for `latin1` even its characters. So the writer sniffs a BOM, else probes the bytes as
strict UTF-8, and when neither answers it **fails closed**. With no `charset` in `.editorconfig`
there is no second opinion to convert to, and a guess would silently corrupt a `cp1251` or `latin1`
file that happens to sit in a `charset = utf-8` tree. A `charset` declaration is the only thing
allowed to name the encoding of a chunk the writer cannot identify, and the report marks it
`(from .editorconfig)` so the model can see the bytes came from the tree, not from the file.
Converting a whole tree is a formatter's job (`dotnet format`, an IDE action), not something a tool
call should do silently.

A declared `charset` names an encoding; it does **not** let the write add a BOM to a file that has
none. The preamble comes from the file's own identity, and in this branch the file has no BOM by
definition — so `charset = utf-8-bom` over a BOM-less file leaves it BOM-less, and the writer refuses
to *invent* one. The VS Code host used to take the preamble from the charset table instead, so the
same approved call produced `6F 6B` in Visual Studio and `EF BB BF 6F 6B` in VS Code; with
`charset = utf-16le` it also injected `FF FE` and encoded the new text as UTF-16 into a body that was
not. Its own `append` could never add a BOM (it concatenates the raw bytes), so the same charset
disagreed with itself inside one host.

What happens to the bytes already on disk depends on the mode, and the difference is not cosmetic:

- `append` / `prepend` concatenate: the existing bytes are never decoded and never re-encoded, so
  everything the file already contained is byte-identical afterwards;
- `overwrite` **deletes** them. Nothing is preserved in that mode — the old encoding is gone, and
  `Encoding:` in the report tells you what the file is *now*.

Appending an empty string is a no-op that touches nothing at all — not even the file's mtime — and
says `Nothing to append to <path>: the content was empty and the file was not modified.` The sentence
names only `append` for both modes on purpose: the caller's intent, "add this text", is the same. A
body that was nothing but a `U+FEFF` counts as empty, because the strip below runs BEFORE this check.
An ABSENT
`content` is a different thing and is refused before the writer is reached: it used to become `""`,
and `overwrite` deliberately does not treat an empty body as a no-op (creating an empty file is
legitimate), so one missing field truncated an existing file to nothing and reported success.

The glue appears only when the side that runs into the other has **no line break of any kind** at its
end: the existing bytes for `append`, the new text for `prepend` (prepending is the side that would run
into the old first line). It
used to test the *dominant* separator instead, so a file that ended with a lone `\n` in a CRLF file
got a second break and a blank line appeared before the appended text — and the two hosts answered
differently on that same input.

A `U+FEFF` a model pasted into the content is stripped **unconditionally**, not only when the target
charset has no BOM. The BOM belongs to the encoding the writer resolved; a character arriving as text
is therefore always a second one — at the start of an overwritten body, or buried inside an append.
For `charset = utf-8-bom` the old guard did not fire at all and the file came out with two BOMs in a
row, which is a stray character before the first C# token. The writer still refuses to *invent* a BOM
and only removes one that arrived as text. Both hosts strip it unconditionally, on both the
new-file and the existing-file path, and both do it before the empty-body guard — a body that was
only a `U+FEFF` is an empty body, not an append of one stray character.

## One separator algorithm (was three)

A line ending is never guessed and never taken from the platform:

- **the MAJORITY wins** — the most frequent of CRLF / LF / CR;
- **ties are broken CRLF > LF > CR**, because a tie means the file has no opinion and a stable answer
  is worth more than a clever one;
- **a text with no line break has no opinion (`null`)**; each caller then supplies its own fallback —
  the writer prefers `.editorconfig` `end_of_line` and only then `\n`, the reader uses `\n`. A file
  without any break therefore cannot be "converted" to something it never had.

One implementation, `TextFileCodec.DetectSeparator`, two call sites: the writer's separator for an
existing file (the `append` / `prepend` glue reuses that very value) and the reader's `Separator`. The count runs
over the file's **BYTES** and only on **character-unit boundaries**, because the encoding decides how
wide a break is: UTF-16 `0D 00 0A 00` is ONE CRLF, while an unaligned search for `00 0A` inside
UTF-16BE text would find a "break" straddling two unrelated characters and invent one that is not
there.

Why one implementation: both hosts used to decide by PRESENCE (`Contains("\r\n")` /
`includes('\r\n')`), so

```
a\r\nb\nc\nd\ne
```

was `crlf` in both — not a divergence, but not a majority either. What HAD drifted was the fallbacks
the two copies had accumulated, and the reader carried a third answer on top of them; one
implementation with no local branch is what removes that class of question.
`Environment.NewLine` and `os.EOL` appear in neither host's **byte-deciding** code, for exactly this
reason: the bytes must not depend on which machine ran the tool.

## `edit_file` identifies a file the same way

`create_file` and `edit_file` MUST agree about what a file IS.
`ToolCore/FileUtils.ReadFileWithMetadataAsync` walks the same ladder as the writer:

```
BOM                                   → that charset, body starts after the preamble
no BOM + NUL byte in the first 1 KB   → Binary, with a SkipReason
no BOM, bytes are strict UTF-8        → utf-8
not UTF-8, .editorconfig declares charset → that charset
not UTF-8, nothing declared           → Binary, with a SkipReason
```

The NUL guard runs only for a file WITHOUT a BOM: ASCII bytes inside UTF-16 contain NULs, so probing
first would declare every BOM-ed UTF-16/UTF-32 file binary. Before this change the read path decoded
with a plain UTF-8 `StreamReader`, which means `edit_file` re-encoded a BOM-less cp1251/latin1 file
into `U+FFFD` bytes and **reported success** — the same defect that had already been fixed on
`create_file`, still live on the edit path, where it destroys the file on the first write.

The VS Code reader (`VSCode/src/toolHandlers/fileReader.ts`) walks the same ladder, including the
`.editorconfig` fallback — it briefly did not, which made a cp1251 file in a `charset = latin1` tree
editable in Visual Studio and unreadable in VS Code. That asymmetry is the reason both hosts own the
ladder exactly once (`encoding.ts` over there) and why neither may grow its own private copy.

The three refusals say what is missing, so the caller can repair the tree instead of retrying:

- `file has no BOM and is not valid UTF-8, and .editorconfig declares no charset. Add charset to .editorconfig, or edit the file with bash.`
- `file has no BOM and is not valid UTF-8, and .editorconfig declares charset '{X}', which is not supported. Declare a supported charset, or edit the file with bash.`
- `file is not valid {charsetName}: {decoder message}` — a BOM named the encoding and the body
  contradicts it. Nothing is decoded with replacement characters; that is precisely what used to get
  written back into the file.

The VS Code reader had the same hole one layer down and it needed a different remedy, because Node
does not throw on bad bytes: `toString('utf8')` substitutes `U+FFFD`, `utf16le` drops an odd trailing
byte without a word and the UTF-32 decoder clamps an out-of-range unit. It therefore **re-encodes what
it decoded and refuses when the bytes do not come back**, which turns that host's silent corruption
into the same refusal the .NET reader already gave. The two write paths are then symmetric too: the
.NET writer encodes with a THROWING fallback, and the VS Code writer calls `encodeStrict` and writes
nothing when it fails — it used to go through the byte-preserving `encode` and turn `привет` into
`??????` in a `charset = latin1` tree, reporting success.

One deliberate asymmetry in the sniffing: an `overwrite` reads only the first 64 KB (both hosts, same
window) because the preamble and the first line breaks always live there and a large file must not be
decoded merely to be replaced. That window decides the BOM, the separator AND the strict-UTF-8
verdict, so a non-UTF-8 byte past it is not detected — an accepted approximation, identical in both
hosts. `append` / `prepend` still read the whole file, because they splice untouched bytes.

The read path also carries the **two-encoding split** out of `FileIdentity`: `FileReadResult.Encoding`
is the SAVING encoding (it carries the preamble, so `SaveFileAsync`'s `StreamWriter` round-trips
exactly ONE BOM), while the body codec emits none. Collapsing the two into one member drops the BOM
of every `utf-8-bom` / UTF-16 file on the first edit.

## Modes

`mode` is a **required** property of the schema; `overwrite` is what `null` means. With `strict: true`
an optional property is expressed by the null union, not by omission, so a call that wants the default
must SEND `"mode": null` — omitting the key is a schema violation, not a silent default.

Accepted values (case-insensitive): `overwrite` (also accepts `create` / `replace`), `append`,
`prepend`. An unknown value is a per-call failure — not an exception that aborts the tool batch —
whose message names the values that are valid, and nothing is written.

The alias table lives in **`Shared/Contracts/FileWriteModeNames.cs`**, not in `ToolCore`. Two
consumers resolve it FROM `Shared`: the host (`FileWriter.ParseMode`, which maps the canonical name to
the `FileWriteMode` enum) and the UI badge that labels the call. A second copy in the UI would drift
silently — the badge would claim `append` for a value the host appended nothing for, or `overwrite`
for a value the host refuses. `ToolCore.Tests/FileWriteModeNamesTests.cs` pins the table and the
agreement between the table and `ParseMode`; it cannot reference the Blazor component, so the badge's
own resolution is pinned separately by `ToolCreateNewFileTests`, region `Mode badge`.

The same table answers a second question: `FileWriteModeNames.IsPrefixOfAKnownMode` separates
"still streaming" (`app`, a prefix of `append`) from "the host refuses this" (`sideways`). Without it
a chunk boundary inside the value made the badge announce a refusal for a call that was never broken.

The VS Code host cannot consume a C# contract, so it keeps the **only other copy**, in
`VSCode/src/toolHandlers/fileWriter.ts` (`parseMode`) — one home per host, exactly like the encoding
code. That copy is pinned by the `parseMode` block in
`VSCode/src/test/toolHandlers/fileWriter.test.ts`, which mirrors the C# cases apart from two
language-shaped differences: it adds the `undefined` nullish case a C# `string?` cannot express, and
it omits the lowercase `'prepend'` and the `"  sideways  "` trim case that
`FileWriteModeNamesTests` carries. An alias added to `FileWriteModeNames` must be added there in the
same change.

The badge is rendered for every mode, including `overwrite`, whenever the block has a file path (the
badge sits in the file header, which needs one): `overwrite` is the default AND the destructive mode
(`overwrite` deletes the old bytes, `append`/`prepend` keep them), so silence would hide the one case
worth warning about. An unknown value is badged with its raw text, trimmed (`"  sideways  "` badges
`unknown mode: sideways`), and a danger style — never labelled `overwrite`, because the host rejects
the call and that label would describe something that did not happen.

The badge names the mode the call REQUESTED, not the effect it had: both hosts ignore `mode` for a
file that does not exist yet (`WriteNew` never reads it, and the VS Code writer reports `created`
regardless), so `append` next to "created successfully" is correct — the tooltip says so explicitly.

`ModeReceived` separates "the argument has not streamed in yet" from "the argument is explicitly
null". Both resolve to `overwrite`, but only the second is a mode the call asked for, so the badge
stays neutral (`tool-mode-pending`) until the argument is present — otherwise a call that will end as
`append` would display the destructive warning for most of its stream.

The same strict-schema rule governs `edit_file`: every property of the top-level object and of each
nested `edits` entry is listed in `required`, and a default is sent as `null` — see
[edit-matching.md](edit-matching.md#schema-and-dryrun).

## `.editorconfig` support

Resolution walks from the file's directory up to the filesystem root, collecting `.editorconfig`
files and stopping at `root = true`; the closest file wins per property. Only two properties are
read, because they are the only ones that decide *bytes*:

- `charset` — `utf-8`, `utf-8-bom`, `latin1`, `utf-16le`, `utf-16be`;
- `end_of_line` — `lf`, `crlf`, `cr`.

`unset` is not a value: it clears an inherited `charset` / `end_of_line` so the caller's default
applies again.

`latin1` is the one name on that list which can never be *detected* — no BOM identifies it, and its
non-ASCII bytes are usually invalid UTF-8. It therefore appears only as a declaration, and only with
the `(from .editorconfig)` marker in the report.

Section patterns support the glob subset `*`, `**`, `?`, `[...]`, `{a,b}`, `{1..9}`. A pattern with
no `/` matches at any depth, as the spec requires — note that `**/` must therefore match **zero**
directories too, otherwise `[*.cs]` would not apply to a file next to its own `.editorconfig`
(this was a real bug in the first implementation, caught by the resolver tests). Matching is
case-insensitive. Anything unsupported does not match, which degrades to the caller's default
instead of guessing.

A numeric range that cannot be expanded behaves in two different ways, and the difference is
deliberate:

- a REVERSED range — `[f{9..1}.cs]` — matches **nothing**. It used to fall into the "too wide to
  expand" branch below and became `[0-9]+`, so the section applied its `charset` to every numbered
  file in the tree, which is the opposite of what the class doc promised (a header that does not
  compile matches nothing);
- a range too wide to expand — a SPAN above 100 (`to - from > 100`), e.g. `{1..200}` — keeps the
  documented `[0-9]+` over-approximation: a small regex instead of a thousand alternatives.

## A rotten `.editorconfig` must not break the write

The tree file arrives with the checked-out repository, so it is untrusted input in both directions —
and "untrusted" covers the READ, not only the parse. The rule is one sentence: **a `.editorconfig`
that cannot be used contributes NOTHING, and the write proceeds with the caller's defaults.** Three
shapes implement it, and all three are tested in both hosts:

| Shape | Why it used to break |
|-------|----------------------|
| Header that cannot compile (`[z-a]x`, `[]`, `[!]`) | `ArgumentException` out of the resolver on the .NET side, `new RegExp` throwing on the JS side, so every write in the tree failed |
| File that cannot be READ (held by another process, ACLs, a share that went away) | `ReadAllText` / `readFileSync` threw out of the resolution walk — the promise above covered only the pattern half |
| A DIRECTORY named `.editorconfig` | `existsSync` answers true for a directory, so the following read threw `EISDIR`. .NET's `File.Exists` answers false, so the VS host was already safe — the VS Code host is the one that had to learn it |

`[!]` is worth its own sentence because it looks like the first row and is not: .NET rejects `[^]` as an
unterminated set and ignores the section, while JavaScript accepts `[^]` as "any character" and APPLIED
it — one header, two different trees. The translator now returns "matches nothing" for an empty class,
and `[z-a]` is the portable way to write a broken header in a test.

Unparseable LINES are skipped without ending the section they sit in, so junk in a hand-edited file
costs one line and not every setting below it. A BOM at the start of the file is removed by the
decoder in the VS host and by `String.prototype.trim` (ZWNBSP is ECMAScript WhiteSpace) in the VS Code
host — two mechanisms, one file shape, despite Visual Studio and Notepad writing such files by default.
That holds for a **UTF-8** BOM only: a UTF-16 `.editorconfig` is decoded by the VS host and read as
NUL-laden junk by the VS Code host, which contributes nothing. The one byte-order shape the two
mechanisms do not share.

## Report

The tool result names the path, the operation and what the file is now, so the model can react without
reading the file back. Samples, shortened to the fields that differ per case:

```
File src/New.cs created successfully. Encoding: utf-8. Line endings: lf.
File log.txt appended to successfully. Encoding: utf-8.
File legacy.txt overwritten successfully. Encoding: latin1 (from .editorconfig).
Nothing to append to log.txt: the content was empty and the file was not modified.
```

Note the message shape: the file comes FIRST and the verb at the end (`File log.txt appended to
successfully.`), so a reader skimming a batch can match result to path without parsing prose.

`Line endings:` appears **only** when `.editorconfig` enforces `end_of_line` AND the write used that
value — either the file's dominant separator already equals it, or the file has **no line break at
all**, in which case the declared value is what the new text was normalised to. The field states the
rule this write APPLIED, so a declared `crlf` on an `lf` file must not be printed as if it had been
applied — that is what the mismatch note below is for, and printing both made one payload contradict
itself. A separator sniffed from the file is that file's OWN state,
not a rule this write applied, so printing it made an ordinary append look like a reformat. Silence
there means the content's own endings went to disk — the writer never invents a newline convention.

A disagreement between the tree and the file is a NOTE, never a silent fix. The note ends with the
part of the file the setting was actually applied to:

- ` The written content used '{X}'.` — `overwrite`;
- ` The existing bytes were left untouched; only the new text used '{X}'.` — `append` / `prepend`.

Those two sentences exist ONLY as the tail of a mismatch note. The VS Code host used to print them on
every existing-file write, which made the same write read differently in the two hosts. Silence is the
normal case: a write that agrees with the tree says nothing extra.

`(from .editorconfig)` is load-bearing: it is the only way `latin1` can appear, because no BOM
identifies it. An encoding reported without the marker was sniffed from the file's own bytes (or is
the UTF-8 default for a new file); one reported with it came from the tree.

## Tests

| What | Where |
|------|-------|
| Resolver: `root = true`, closest-wins, pattern subset, unknown charset, case-insensitivity, reversed vs wide numeric range, an UNREADABLE config, a directory named `.editorconfig`, a BOM, junk lines inside a section | `ToolCore.Tests/EditorConfigResolverTests.cs` |
| Writer: BOM/no-BOM for a new file, `.editorconfig` encoding + EOL, byte-level `append`/`prepend`, mode parsing, `Line endings:` only when enforced | `ToolCore.Tests/FileWriterTests.cs` |
| The mode alias table and its agreement with the host (`ParseMode`) | `ToolCore.Tests/FileWriteModeNamesTests.cs` |
| The mode alias table in the VS Code host (the only other copy) | `VSCode/src/test/toolHandlers/fileWriter.test.ts`, `parseMode` |
| The mode badge: every alias, unknown value shows its raw text, overwrite warns / append succeeds | `UIBlazor.Tests/Components/ToolViews/ToolCreateNewFileTests.cs`, region `Mode badge` |
| "Still arriving" vs "final": the ellipsis while the arguments are open, the real verdict at the approval gate | `UIBlazor.Tests/Components/ToolViews/ToolCreateNewFileTests.cs`, `ModeBadge_IsNeutral_*` and `ModeBadge_PartialValueOnAFinishedCall_*`; the wiring `ToolCall.ArgumentsOpen` in `ToolCallBlockTests.CreateFileTool_ReportsTheArgumentsAs*` |
| The badge receives the call's `mode`, including a tolerant non-string value that must not blank the block | `UIBlazor.Tests/Components/ToolViews/ToolCallBlockTests.cs`, `CreateFileTool_*` |
| The single homes: splitter parity, majority separator counted over bytes (incl. UTF-16), strict-UTF-8 probe, charset table, byte helpers | `ToolCore.Tests/TextFileCodecTests.cs` |
| Edit read path: binary refusal leaves the bytes alone, a declared `latin1` round-trips byte-exact, `utf-8-bom` keeps exactly ONE BOM | `ToolCore.Tests/FileUtilsTests.cs` |
| Edit batch orchestration: ordering, per-edit refusals, counters, the model-facing sentences | `ToolCore.Tests/EditBatchTests.cs` |
| Same rules in the VS Code host (stubbed fs) | `VSCode/src/test/toolHandlers/fileWriter.test.ts`, `fileOps.test.ts` |
| The fixture-path translation that lets the mock tests run on the ubuntu runner, incl. the Unix half with the platform as a parameter | `ToolCore.Tests/MockPathsTests.cs` |

All three .NET test files above use `MockFileSystem`, which inherits the HOST's path rules and has no
platform override — so every path handed to the mock goes through `MockPaths.Mock`. Untranslated, a
Windows literal is not an absolute path on Linux, every `.editorconfig` lookup misses, and the whole
file silently resolves to defaults (which is exactly how 33 tests failed on CI while passing locally).
`AGENTS.md` states the rule; the reason and the local simulation command are there too.
| Glob translator (`globToRegex`) | `VSCode/src/test/toolHandlers/editorConfig.test.ts` |

Nothing above exercises the VS host's *wiring*. `InvAit` targets .NET Framework 4.8 against the VS SDK
and stays deliberately logic-free, so a regression in `ToolExecutor` between the JSON payload and
`EditBatch`/`FileWriter` is not caught by a unit test: the unit tests pin the tool core, not the hop
into it. `InvAit.Tests/InvAit.Tests.csproj` exists as an EMPTY scaffold — zero `.cs` files, not listed
in `InvAit.slnx` — and is NOT that guard. The guard is `ToolCore.Tests` + `UIBlazor.Tests` + the VS
Code mocha suite, which is exactly why real logic belongs in `ToolCore`: `EditBatch` is the newest
example, and the host shrank to parse / resolve / check / read / apply / save. Tracked in the
local-only `todo.md` (not linked: it is untracked), see
[edit-matching.md](edit-matching.md#known-limitations).
