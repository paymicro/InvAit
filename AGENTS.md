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
 │ UniversalDiff    │          └───────────────┘
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
- **`ToolCore`** (netstandard2.0) — Reusable tool logic: `FileUtils`, `UniversalDiffParser` (fuzzy diff), `RoslynSearchService`, `McpHostSupervisor` (McpHost lifecycle), `ProcessExecutor`. `McpProcessManager` is obsolete (Standalone CLI only).
- **`Shared`** — Contracts: `VsRequest`/`VsResponse`/`VsMessage`, `BuiltInToolEnum`, `BasicEnum`, `AppMode`, `ToolMetadata`, `DiffEdit`, `ReadFileParams`, MCP models (`Contracts/Mcp`), McpHost wire protocol (`Contracts/McpHost`) + `Ipc.FrameCodec`.
- **`McpHost`** (net10 exe, `InvAit.McpHost.exe`) — Out-of-process MCP client host on official `ModelContextProtocol` SDK. Named-pipe IPC (length-prefixed JSON): `ping/status/list_tools/call_tool/stop_server/stop_all/shutdown`. Registry lazily starts stdio or HTTP/SSE servers by fingerprint; idle-reap 10 min. One host per VS instance.
- **`ToolCore.Standalone`** — CLI for testing MCP/exec without VS.

## Communication

JSON-RPC over WebView2. UI → VS: `VsRequest` (Action, Payload, CorrelationId). VS → UI: `VsResponse` (result) or `VsMessage` (push, e.g. `UpdateCodeContext`).

InvAit ↔ McpHost: length-prefixed JSON over named pipe (`invait.mcp.{guid}`), supervisor watchdog ping + auto-restart with backoff.

**Flow:** LLM tool_call → UIBlazor → VsBridge → WebView2 → ToolExecutor → McpHostSupervisor → pipe → McpHost → MCP server.

## App Modes

| Mode | Categories |
|------|------------|
| Chat / Plan | `ReadFiles`, `ModeSwitch` |
| Agent | All |

Per-session, switched via `switch_mode` tool.

## Tools

Defined in `BuiltInAgent.cs`, schemas auto-generated from `BuiltInToolDefs.cs`.

Approval per category: Allow (default), Ask (`Execution`/`DeleteFiles`), Deny. MCP tools from `~/.agents/mcp.json`, registered as `mcp__{server}__{tool}`. Config supports BOTH roots simultaneously in one file: OpenCode-style root `"mcp"` and Claude/Cursor-style `"mcpServers"` are merged (same name in both → `"mcp"` entry wins). Type aliases: local group `local`/`stdio`/absent (default; `command` required, string or array) or remote group `remote`/`http`/`streamable-http` (`url` required, optional `headers`/`enabled`; `oauth` unsupported). Tool disable states live in localStorage only.

## Skills & Rules

Skills: `*SKILL.md` with YAML frontmatter. Local `{solutionDir}/**/skills/**` (priority) → global `~/.agents/skills/**`. Metadata in prompt, content via `read_skill_content`. Rules: global `~/.agents/rules.md` + local `.agents/rules.md`.

## Sessions

`localStorage`, max 5 recent. Compression when tokens exceed threshold. Retry: exponential backoff (2s→5s→10s→20s).

## Development

**Build:** Publish `UIBlazor` → `wwwroot` mapped to `blazorui.local`. Build `InvAit` → VSIX.

**Test (MTPv2):** `dotnet test --project UIBlazor.Tests/UIBlazor.Tests.csproj` or `dotnet test --project ToolCore.Tests/ToolCore.Tests.csproj` (integration tests spawn real test MCP servers). Or build and run *Tests.exe

**Conventions:**
- **Diff (`edits`):** Fuzzy matching (trim + case-insensitive). `approximateLine` hint ±5, then full-file search. Applied bottom-to-top.
- **Tool Calling:** OpenAI-compatible, `strict: true`. Optional params use union types (`["string","null"]`).
- **Limits:** Files truncated 2000 lines. Tool results 300 KB. Bash output 30 KB.

**Adding tools:**
1. Add name to `BuiltInToolEnum.cs` (or `BasicEnum.cs` for UI-only).
2. Add method to `BuiltInToolDefs.cs` (`[DisplayName]`, `[Description]`).
3. Add `Tool` to `BuiltInAgent.cs` (`Category`, `ExecuteAsync`).
4. Implement in `ToolExecutor.cs` (VS) or `InternalExecutor.cs` (UI-only).
