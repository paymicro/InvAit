# Bash output sanitizing

What the model is allowed to see of a bash command's output, and why the temp-script path is cut
out of it.

Owner of this topic: any change to `ToolCore/BashOutputSanitizer.cs`,
`VsCode/src/toolHandlers/bashOutput.ts`, or the temp-script naming in either host belongs here in
the SAME change.

## The problem

Both hosts run a bash command by writing it to a temp file and handing that file to `sh`
(`ProcessExecutor.ExecuteBashAsync`, `bash()` in `bash.ts`). The file exists to dodge quoting and
command-line-length limits, and it is deleted in `finally` right after the run.

`sh` prefixes every diagnostic with the name of the script it is executing:

```
C:\Users\pc\AppData\Local\Temp\invait_8f45813bcf3141d0841baff67db24fae.sh: line 1: strings: command not found
```

That prefix is pure noise for the reader:

| Why it is noise | |
|-----------------|---|
| The file is already deleted | the model cannot read it, so the path is not actionable |
| It is a machine-specific absolute path | it leaks the user's home directory into the transcript and the prompt |
| It eats context | one long path per diagnostic line, and a failing script produces many |
| It hides the signal | `line 1: strings: command not found` is the whole message |

## The rule

> Strip by **identity**, never by **shape**.

The sanitizer removes the temp file it created — the exact path handed to `sh`, plus the
`invait_<...>.sh` name pattern — and nothing else. It must never remove "any `.sh`": an error in the
user's own `build.sh` carries the file name as its most useful part, and a shape-based rule would
delete exactly the information the message exists to carry.

| Input | Output |
|-------|--------|
| `<temp>: line 1: strings: command not found` | `line 1: strings: command not found` |
| `<temp>: line 2: cd: /nope: No such file or directory` | `line 2: cd: /nope: No such file or directory` |
| `build.sh: line 3: dotnet: command not found` | unchanged |
| `/home/pc/project/scripts/deploy.sh: line 7: kubectl: ...` | unchanged |
| `myinvait_abc.sh: line 1: boom` | unchanged — the prefix is not at a name boundary |

## Where it lives, and why not in the UI

The sanitizer runs in the HOST, immediately after the process exits — not in `UIBlazor` when the
result arrives.

The host is the only layer that knows the exact path it passed to `sh`. A UI-side filter would have
to recognise the path by shape, i.e. it would need the very rule this design rejects, and it would
apply to every tool result rather than to the one channel that produces the noise. `UIBlazor` also
receives results from the VS Code host and from MCP servers, where a `*.sh` mention may be genuine
output that must survive.

The cost of the host-side choice is duplication: the two hosts share no code, so the rule exists
twice, with a comment in each file pointing at the other. That is the same trade `timeout.ts`
already makes for the `timeoutMs` channel rule, and for the same reason — a fix that lands in one
channel only is worse than a duplicated constant.

## Traps the implementation handles

| Trap | What happens without the guard |
|------|-------------------------------|
| The name pattern must cover BOTH file-name shapes | the C# host writes `invait_<32 hex>.sh`, the Node host `invait_<ms>_<rand>.sh`; pinning one shape leaves the bug alive in the other channel |
| The exact path must be stripped as well as the pattern | a temp directory containing spaces (`C:\Users\John Doe\...`) is not matched whole by the name pattern, so a `\`-fragment would survive |
| The forward-slash variant must be stripped | Git Bash reports `C:/Users/...`, and the exact-path pass compares strings |
| The trailing `:` belongs to the path | leaving it produces `: line 1: ...` — a leading colon that reads like a syntax error |
| `[ \t]?`, not `\s?`, after the path | `\s` matches the newline and glues two output lines into one |
| The character class must include `:` and the separators | otherwise the match starts at the first directory and leaves a `C:\` fragment behind |
| The path prefix must end with a separator, and the match must start on a boundary | the `my` tail of `myinvait_abc.sh` lands in the path class, so a user file loses its name — the sanitizer destroying the very information it protects |
| The extension match must be case-insensitive | a shell that upper-cases the echoed name (`INVAIT_....SH`) keeps the path |
| Lines emptied by the strip must be dropped | `echo $0` prints only the path; the sanitizer would replace noise with a blank line |
| Lines that were empty already must stay | they are part of the command's output, not our residue |
| The Node regex is `/g` and stateful | `lastIndex` carries between calls, so a missing reset makes the second call silently skip the match |

## Accepted limitations

- A user file named `*invait_*.sh` loses its name too. The `invait_` prefix is our reserved marker,
  so such a file counts as ours. Narrowing it would require passing the exact path everywhere, which
  the name pattern exists to avoid.
- Only the temp-script reference is stripped. Other machine-specific noise (absolute paths inside a
  compiler's own diagnostics, ANSI escapes) is out of scope here: `NO_COLOR`/`TERM=dumb` already
  suppress the escapes, and rewriting a compiler's paths would be guessing.

## Test seams

- `ToolCore.Tests/BashOutputSanitizerTests`: both halves of the rule (our file disappears, the user's
  file survives), the forward-slash and spaced-directory variants, every occurrence, the emptied-line
  and pre-existing-blank-line cases, the no-glue case, and the other host's file-name shape.
- `ToolCore.Tests/ProcessExecutorTests`: an end-to-end run whose stderr must not contain `invait_` or
  `.sh` while keeping `line 1`, and a run that must keep the user's own script name.
- `VsCode/src/test/toolHandlers/bashOutput.test.ts`: the same matrix on the Node side, plus the
  stateful-`/g` reuse case.
- `VsCode/src/test/toolHandlers/bash.test.ts`: the handler strips the path it actually passed to
  `spawnSync`, and keeps a user script's name.
