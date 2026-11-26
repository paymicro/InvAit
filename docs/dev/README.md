# Developer documentation

Deep documentation of the parts of this solution that are hard to reason about from a single file:
the agent turn loop, the LLM transport (timeouts, retries, error classification), the session model,
the chat service contract, the transcript's render cost and why it folds its middle — and four
documents on the tool layer, where behaviour is invisible in the UI and expensive to get wrong twice,
once per host: how long a tool may run ([tool-timeout.md](tool-timeout.md)), what the model sees of a
bash output ([bash-output.md](bash-output.md)), which line an `edit_file` search lands on
([edit-matching.md](edit-matching.md)) and which bytes `create_file` writes — over the one file
identity both file tools share ([file-writes.md](file-writes.md)).

`AGENTS.md` carries the architecture map and the rules that must not be broken. These documents
carry the *why*: the invariants, the failure modes they prevent, and the traps that produced them.

| Document | Answers | Update when |
|----------|---------|-------------|
| [agent-turn-loop.md](agent-turn-loop.md) | Who owns the LLM turn cycle, what happens between turns, why the loop left the gateway | Turn-loop shape, who calls `GetTurnCompletionsAsync`, between-turn mutations |
| [llm-transport.md](llm-transport.md) | Timeout ownership, retry policy, error classification, the UI trace of a failed turn | Timeout ownership, retry budget, error classification, `LlmApiException` |
| [session-and-context.md](session-and-context.md) | `BuildWire`, compression planning, the system-prompt cache, token accounting | `BuildWire` validation rules, compression planning, prompt cache key |
| [chat-service.md](chat-service.md) | `IChatService` surface: what each member guarantees and who may call it | An `IChatService` member (add/remove/change a guarantee) |
| [message-window.md](message-window.md) | Why the transcript folds its middle, the protected set, the run pin, the auto-scroll on first overflow, the shared divider-row style, and the accepted limitations | Who is materialised in the transcript, the protected set, a window constant, the auto-scroll watcher, the plaque/retry-note divider style |
| [render-performance.md](render-performance.md) | What one streamed token costs: the per-message notification, the collapsed-`Details` body, the markdown and mermaid gates, the sub-agent throttles, and the measurements behind them | A change-detection path: `ShouldRender`/`HasChanges`, a render gate, a throttle interval, anything that runs per streamed token |
| [tool-timeout.md](tool-timeout.md) | Who limits how long a tool may run: the three clocks, the `timeoutMs` channel rule, the client grace, the per-tool defaults | A tool timeout: who enforces it, the `timeoutMs` payload field, a new per-tool default, the client grace |
| [bash-output.md](bash-output.md) | What the model sees of a bash command's output: why the temp-script path is stripped, the strip-by-identity rule, and why the sanitizer lives in the host and not in the UI | Bash output shaping: the temp-script path, `BashOutputSanitizer`/`bashOutput.ts`, the temp-file naming |
| [edit-matching.md](edit-matching.md) | How an `oldStr`/`newStr` becomes a file change: the single normalisation point, the trailing-newline rule, the matching/replacement semantics, why an ambiguous edit is refused, how the batch orchestration is kept testable, how the VS Code port is kept in step, and how a malformed `edits` or `files` payload is refused in words the model can act on | Edit matching: how `oldStr`/`newStr` becomes lines, a matching/replacement rule, the ambiguity policy, the `edits`/`dryRun` schema, the `edits` shape refusals and their wording (`EditPayload`), the `read_files` shape refusals (`ReadFilesPayload`) and the wording helpers both share (`JsonShape`), the `replaceAll`/`dryRun` badges, the batch orchestration in `EditBatch`, a rule ported to `diffMatch.ts` |
| [file-writes.md](file-writes.md) | How encoding and line endings are decided: `.editorconfig` resolution, the one file identity that `create_file` and `edit_file` both use — BOM ladder, strict-UTF-8 probe, refusal — the majority line-break rule, byte-level `append`/`prepend`, and what the tool reports back | File writing and identity: a `.editorconfig` key or pattern rule, the encoding/line-ending policy, the identification ladder or its refusal, a write mode, its alias table (`FileWriteModeNames` in `Shared`, `parseMode` in the VS Code host), the `mode` schema, the `create_file` mode badge, the `Line endings:`/note-tail report |

## Conventions

- **Present tense, current behaviour.** A document describes what the code does today. Anything
  planned but not implemented lives in a section titled `## Proposed` and says so explicitly.
- **Every invariant names the failure it prevents.** "Do not do X" is unreviewable; "X was done and
  it broke Y" survives a refactor of the surrounding code.
- **Code wins.** If a document and the code disagree, the code is right and the document is a bug.
  Fix it in the same change that moves the behaviour.
- **No duplicated tables.** Parameters and their defaults have one authoritative table, in the
  document that owns them; other documents link to it.

## Keeping these current

A change needs a documentation edit when it touches the "Update when" column of that table. When two
rows seem to apply, the document that owns the RULE wins; the other one links to it.
