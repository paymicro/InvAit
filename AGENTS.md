## Architecture

VS Extension (VSIX) embedding Blazor WebAssembly UI in WebView2.

```
  VS Host (InvAit)              Frontend (UIBlazor)            External
 ┌──────────────────┐         ┌──────────────────────┐    ┌────────────────┐
 │ InvAitPackage    │         │ ChatService ─────────┼───▶│ OpenAI API     │
 │ ChatControl ─────┼──JSON──▶│ BuiltInAgent         │    │ (SSE streaming)│
 │ ToolExecutor     │  RPC    │ VsBridge             │    └────────────────┘
 │ ContextPublisher │◀─(WV2)──│ ToolManager          │
 └──────┬───────────┘         └──────────┬───────────┘    ┌─────────────────┐
        │                                │                │ MCP Servers     │
        ▼                                ▼                │ (stdio/HTTP-SSE)│
 ┌──────────────────┐   pipe   ┌───────────────┐          └─────────────────┘
 │ ToolCore         │  JSON-RPC│ McpHost       │◀────────────┘
 │ McpHostSupervisor│◀─────────│ (net10 exe,   │
 │ FileUtils        │          │ MCP SDK)      │
 │ EditApplier      │          └───────────────┘
 │ RoslynSearch     │         ┌──────────────────────┐
 │ ProcessExecutor  │         │ Shared (Contracts)   │
 └──────────────────┘         │ VsRequest/Response   │
                              │ BuiltInToolEnum      │
                              │ BasicEnum, AppMode   │
                              │ ToolMetadata, Mcp*   │
                              └──────────────────────┘
```

### Projects

- **`InvAit`** — VS backend. `ToolExecutor` dispatches tool calls via `switch` on `VsRequest.Action`. `ChatControl` hosts WebView2 and routes JSON-RPC. `VsCodeContextPublisher` pushes code context (throttled 500ms). `SolutionStructure` builds solution tree.
- **`UIBlazor`** — Blazor WASM frontend. `ChatService` manages sessions, SSE streaming, tool_calls. `BuiltInAgent` defines tools (schema + `ExecuteAsync` delegate). `BuiltInToolDefs` auto-generates JSON schemas from C# methods via reflection. `VsBridge` communicates with VS via WebView2. `ToolManager` handles tool filtering by mode and approval. `SystemPromptBuilder` assembles system prompt. `InternalExecutor` handles UI-only tools (`switch_mode`, `ask_user`).
- **`ToolCore`** (netstandard2.0) — Reusable tool logic: `FileUtils`, `EditApplier`/`EditBatch` (fuzzy diff and its batch), `FileWriter`/`FileIdentity`/`TextFileCodec`/`TextLines`, `RoslynSearchService`, `McpHostSupervisor` (McpHost lifecycle), `ProcessExecutor`.
- **`Shared`** — Contracts: `VsRequest`/`VsResponse`/`VsMessage`, `BuiltInToolEnum`, `BasicEnum`, `AppMode`, `ToolMetadata`, `DiffEdit`, `ReadFileParams`, `FileWriteModeNames` (the `create_file` mode alias table, shared by the host and the UI badge), MCP models (`Contracts/Mcp`), McpHost wire protocol (`Contracts/McpHost`) + `Ipc.FrameCodec`.
- **`McpHost`** (net10 exe, `InvAit.McpHost.exe`) — Out-of-process MCP client host on official `ModelContextProtocol` SDK. Named-pipe IPC (length-prefixed JSON): `ping/status/list_tools/call_tool/stop_server/stop_all/shutdown`. Registry lazily starts stdio or HTTP/SSE servers by fingerprint; idle-reap 10 min. One host per VS instance.
- **`invaitcode`** — VS Code extension (TypeScript). Hosts the same Blazor WASM UI in a webview panel via a local static HTTP server. `extension.ts` handles tool dispatch, context publishing, and network proxy (streaming SSE through `postMessage`). `toolHandlers/` implements file/git/bash/MCP tools in Node.js, routed by `toolDispatcher.ts`. `DynamicEnvironmentHttpMessageHandler` (in UIBlazor) routes HTTP requests through the extension host when running inside VS Code. localStorage is proxied to `globalState` for persistence.

### Agent turn loop

**The turn loop lives in the CALLER, not in a gateway.** `ChatService.GetTurnCompletionsAsync`
streams exactly ONE LLM turn: it rebuilds the request from the session at call time
(`ConversationSession.BuildWire`), resolves the tools of the session's CURRENT mode and makes a
single gateway call. It executes no tools.

Orchestration belongs to two callers, which share the same shape:

| Caller | Loop |
|--------|------|
| `AIChat.RunTurnsAsync` | main agent |
| `AfSubAgentOrchestrator.RunBranchAsync` | sub-agent branch |

Each iteration: check compression → build wire → stream one turn → collect tool calls →
approve + execute → append results → repeat. Consequences to keep in mind when editing:

- Every between-turn mutation (compression, edit, delete, interject, `switch_mode`) reaches the
  model on the NEXT turn, because the wire is rebuilt from the session each time. There is no
  cached history to invalidate.
- `VisualChatMessage` objects are the single source of truth; `BuildWire` mutates them (orphan
  tool-call stubs) and dedupes by REFERENCE (`Id` is `[JsonIgnore]`, regenerated on load).
- The system prompt is cached in `SystemPromptBuilder` by `(Mode, canDelegate, revision)`;
  `revision` bumps on IDE context push and profile edit. Rebuilds are what break the provider
  prompt cache, so keep consecutive builds byte-identical.
- Timeout: `AfCompletionsAdapter` is the ONLY owner. Transport clients get no timeout. Streaming
  uses an IDLE window (re-armed per chunk), non-streaming a total budget, `HttpTimeoutSec <= 0`
  disables it. A burned window is an `LlmApiException`; the retry decision is
  `LlmApiException.IsRetryable` (only a permanent 4xx or a non-`LlmApiException` bug skips the
  budget).

**Deep documentation lives in [`docs/dev/`](docs/dev/README.md)** — read the relevant file before
changing a subtle area, and update it in the SAME change. `docs/dev/README.md` owns the index: it
lists every document, what each one answers, and which changes require which edit.

## Communication

JSON-RPC over WebView2. UI → VS: `VsRequest` (Action, Payload, CorrelationId). VS → UI: `VsResponse` (result) or `VsMessage` (push, e.g. `UpdateCodeContext`).

InvAit ↔ McpHost: length-prefixed JSON over named pipe (`invait.mcp.{guid}`), supervisor watchdog ping + auto-restart with backoff.

**Flow:** LLM tool_call → UIBlazor → VsBridge → WebView2 → ToolExecutor → McpHostSupervisor → pipe → McpHost → MCP server.

## App Modes

| Mode | Categories |
|------|------------|
| Chat / Plan | `ReadFiles`, `ModeSwitch`, `Mcp` |
| Agent | All |

Per-session, switched via `switch_mode` tool.

**MCP tools are NOT filtered by mode**: `ToolManager.EnableInMode` allows `ToolCategory.Mcp` everywhere, so a writing MCP server (filesystem, git, database) stays available in Chat/Plan. The Plan-mode instruction "do not change files" is a PROMPT rule, not an enforced one — do not describe it as a sandbox.

## Tools

Defined in `BuiltInAgent.cs`, schemas auto-generated from `BuiltInToolDefs.cs`.

Approval per category: Allow (default), Ask (`Execution`/`DeleteFiles`), Deny. MCP tools from `~/.agents/mcp.json`, registered as `mcp__{server}__{tool}`. Config supports BOTH roots simultaneously in one file: OpenCode-style root `"mcp"` and Claude/Cursor-style `"mcpServers"` are merged (same name in both → `"mcp"` entry wins). Type aliases: local group `local`/`stdio`/absent (default; `command` required, string or array) or remote group `remote`/`http`/`streamable-http` (`url` required, optional `headers`/`enabled`; `oauth` unsupported). Tool disable states live in localStorage only.

## Skills & Rules

Skills: `*SKILL.md` with YAML frontmatter. Local `{solutionDir}/**/skills/**` (priority) → global `~/.agents/skills/**`. Metadata in prompt, content via `read_skill_content`. Rules: global `~/.agents/rules.md` + local `.agents/rules.md`.

## Sessions

`localStorage`, max 5 recent. Compression when tokens exceed threshold. Retry: exponential backoff (2s→5s→10s→20s).

## Development

**Build:** Publish `UIBlazor` → `wwwroot` mapped to `blazorui.local`. Build `InvAit` → VSIX.

**Test (MTPv2):** `dotnet test --project UIBlazor.Tests/UIBlazor.Tests.csproj --filter-class "*"` or `dotnet test --project ToolCore.Tests/ToolCore.Tests.csproj` (integration tests spawn real test MCP servers). Or build and run *Tests.exe

**MCP integration tests (ToolCore.Tests):** `McpClientRegistry*`, `McpHostSupervisor*`, `HttpClientTransport*` spawn real child processes (McpHost pipe, stdio/HTTP echo servers) and are the only process-dependent tests. Set `INVAIT_SKIP_INTEGRATION=1` to run the fast unit-only suite; CI runs them in a separate time-bounded step (see `IntegrationGate`). Tool timeouts in those tests are capped at 30 s on purpose — the production default (10 min) would turn a stuck MCP server into a CI hang.

**Conventions:**
- **Diff (`edits`):** fuzzy matching (trim + case-insensitive); a single-line search matches as a SUBSTRING, so only the matched part is replaced and the rest of the line (a trailing `// comment` included) survives; an EXACT trimmed match replaces the whole line verbatim, so the replacement brings its own indentation. Matching order: the `approximateLine` ±5 window FIRST, then an exact whole-line block over a fragment hit, then REFUSAL with the line numbers — no "first match anywhere" fallback, and `replaceAll: true` is the only way to mean all of them (EXCEPT an all-empty search block, which always settles on ONE line and cannot be widened) — and it is PER EDIT (`DiffEdit.ReplaceAll`, `bool?`); there is no batch-wide flag, the one that existed was read from a payload key the schema never declared. Applied in descending `approximateLine` order: hinted edits first, highest line first; edits with no hint last, in request order. `EditApplier.SplitEditText` delegates to `TextLines.SplitEditText` — the splitter exists once; `FindInFile` no longer exists (the tests are still named `FindInFile_*`). `edit_file` cannot delete a lone line — rewrite with `create_file` — see [docs/dev/edit-matching.md](docs/dev/edit-matching.md).
- **File writes (`create_file`):** a new file takes `charset`/`end_of_line` from `.editorconfig` (UTF-8 without BOM by default, never a hard-coded BOM); an existing file is identified first — BOM, else strict-UTF-8 probe, else REFUSED when no `charset` is declared — so it is never silently re-encoded. `edit_file` identifies a file the SAME way, because a wrong decode there rewrites it. `append`/`prepend` concatenate its bytes, `overwrite` deletes them. `mode` is required in the schema: the default is sent as `"mode": null`, not omitted. An ABSENT `content` is REFUSED (an empty one is legal): it used to become `""`, and `overwrite` of an empty body is not a no-op, so one missing field truncated the file and reported success. Every leading `U+FEFF` in the content is ALWAYS stripped (all of them, not just the first) — in BOTH hosts, on the new-file AND the existing-file path, and BEFORE the empty-body guard (a body that was only a `U+FEFF` is an empty body); the BOM belongs to the resolved encoding, so a character arriving as text is a second one. `Line endings:` states the rule the write ENFORCED, so a declared `end_of_line` the file does not use goes into the mismatch note instead of that field — see [docs/dev/file-writes.md](docs/dev/file-writes.md).
- **File identity has ONE home per host:** BOM ladder + two-encoding split in `ToolCore/FileIdentity.cs`, strict encode/probe + charset table + separator algorithm (majority, ties CRLF > LF > CR, counted over BYTES) + byte helpers in `ToolCore/TextFileCodec.cs`, the splitter in `ToolCore/TextLines.cs`; in VS Code all of it is `VSCode/src/toolHandlers/encoding.ts`, which imports nothing. Do not add a second copy — two copies of an encoding decision drift silently. The `create_file` **mode alias table** is the same shape one level up: `Shared/Contracts/FileWriteModeNames.cs` is the single home for the .NET host AND the Blazor badge (which must label a call exactly as the host will execute it), `VSCode/src/toolHandlers/fileWriter.ts` (`parseMode`) is the single home in the VS Code host. Do not add a third copy.
- **Tool Calling:** OpenAI-compatible, `strict: true`. Optional params use union types (`["string","null"]`), which means a default is SENT as `null` and never expressed by omission — nested objects such as `edits` list every property in `required` for the same reason. The FILE-TOOL views (`ToolCallBlock`: `edit_file`, `create_file`) read a call's arguments FIELD BY FIELD (`ReadProperty` + `GetString`/`GetInt`/`GetBool`, case-insensitive) and never through `DeserializeAnonymousType` — that path is where one wrong type inside `edits` answered ALL defaults and asked the user to approve a call with no path and no diff; `ask_user`, `delegate_task` and `bash` still deserialize into an anonymous type. A value cut by a chunk boundary is rendered as "still arriving" (`FileWriteModeNames.IsPrefixOfAKnownMode`) while `ToolCall.ArgumentsOpen` is true — the flag `PrepareToolsForApprovals` clears once the arguments are cleaned and final; after that the same value IS the refusal the host would give, because it is what the model actually sent.
- **Limits:** Files truncated 2000 lines. Tool results 300 KB. Bash output 30 000 characters per stream (stdout and stderr separately), the BEGINNING is dropped and the cut is announced in the output. Command limit comes from `CommonOptions.ToolTimeoutMs` (payload field `timeoutMs`, host kills at it, client waits `+30 s`) — see [docs/dev/tool-timeout.md](docs/dev/tool-timeout.md).
- **`InvAit` stays logic-free:** net48 + VS SDK with no usable test harness, so real logic belongs in `ToolCore` where `ToolCore.Tests` covers it — `ToolCore/EditBatch.cs` is the model (the host only parses, resolves, checks, reads, applies, saves); `InvAit.Tests/` is an EMPTY scaffold, not a guard. See [docs/dev/edit-matching.md](docs/dev/edit-matching.md#the-batch-lives-in-toolcore).

**Adding tools:**
1. Add name to `BuiltInToolEnum.cs` (or `BasicEnum.cs` for UI-only).
2. Add method to `BuiltInToolDefs.cs` (`[DisplayName]`, `[Description]`).
3. Add `Tool` to `BuiltInAgent.cs` (`Category`, `ExecuteAsync`).
4. Implement in `ToolExecutor.cs` (VS) or `InternalExecutor.cs` (UI-only).
