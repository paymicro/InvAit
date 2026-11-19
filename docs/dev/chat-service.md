# IChatService

The boundary between the UI/orchestration layer and the LLM stack. Two callers depend on it: the
main agent (`AIChat`) and the sub-agent orchestrator (`AfSubAgentOrchestrator`).

## Members

### `Session`

The session the user is currently looking at. A **run never reads this property**: a run is pinned
to the session it started in, and `NewSessionAsync` is blocked while a run is active. Everything
that mutates during a run takes a `ConversationSession` argument instead.

### `GetTurnCompletionsAsync(session, live, tools, capture, ct)`

Streams exactly **one** LLM turn. See [agent-turn-loop.md](agent-turn-loop.md).

- `live` — the message the caller is about to write into; excluded from the request by reference.
- `tools` — explicit tool list (a sub-agent branch always passes its own). `null` resolves the
  enabled tools of the session's **current** mode, which is what makes a `switch_mode` take effect
  on the next turn.
- `capture` — reset at entry, filled during the turn.
- Executes **no** tools. Tool calls flow outwards through `capture.AccumulatedToolCalls`.

### `ProcessStreamAsync(message, deltas, callbacks..., capture, ct)`

Applies the deltas of one turn to one message: content and reasoning appended monotonically, tool
calls mirrored, timings stamped, and the message **always closed** (`IsStreaming = false`) — on
success, on cancellation and on failure. A message left streaming is filtered out of `BuildWire`
and freezes on screen.

Callbacks: `onContentUpdate` (new text for segment parsing), `onToolCallsUpdate` (calls are ready
to render), `onStateChange` (something changed, re-render). `onStateChange` also fires for updates
with no payload, which is how a reasoning-only delta reaches the UI.

### Timings of a turn

All timing maths lives in `MessageTimingsCalculator.Apply(timings, capture, elapsedMs, firstTokenMs)`
and nowhere else. The badge is the user's only evidence of model speed, so the contract is explicit
and unit-tested (`MessageTimingsCalculatorTests`):

- `Total` — wall clock of the whole turn, including the wait for the first token.
- `FirstToken` — that wait alone. It is latency, not generation: a provider that queues for 3 s did
  not generate anything for 3 s.
- `Tokens` — the FULL generation (answer **and** reasoning). How much of it lands in the context is
  `ConversationSession.TotalTokens`, which excludes reasoning.
- `TokensInSec` — `Tokens` over the **generation window** (first token → last token). The rule that
  keeps the number meaningful: **the tokens counted must be the tokens timed**. The window starts at
  the first token of any kind, so reasoning tokens are covered by the time they actually took.
- The window has a floor of 50 ms only to keep the division finite when the whole answer arrives in
  one delta. A floor of one second used to under-report every sub-second answer by exactly the token
  count, and short answers are the common case.

### `NeedsCompression(session)` / `CompressSessionAsync(...)`

`NeedsCompression` is a pure check: `TokensToCompress > 0 && session.TotalTokens > TokensToCompress`.
The caller decides *when* to ask; the service does not compress on its own.

`CompressSessionAsync` has three overloads — current session, explicit session (branches), and a
capture-taking variant. All collapse the history in place and re-arm `TotalTokens`. Details:
[session-and-context.md](session-and-context.md#compression).

### `CompressionStarted` / `CompressionCompleted`

`CompressionStarted` fires before the summary request; `CompressionCompleted` fires after the
history was rewritten, for **every** path (manual `/compress`, the mid-run gate, a sub-agent branch).
The main chat has no other indicator for the invisible paths, so a missing event looks like a frozen
UI.

### `GetCompletionsAsync(capture, ct)`

Convenience wrapper over `GetTurnCompletionsAsync` for the current session, no live message, tools
resolved from the mode. The caller still owns the turn cycle.

### `SaveSessionAsync` / `LoadSessionAsync` / `NewSessionAsync` / `DeleteSessionAsync`

Persistence. Autosave is an invariant of the session model, not a duty of callers: structural
mutations mark the session dirty and a coalescing pump writes it. `SaveSessionAsync` forces an
immediate flush.

`LoadSessionAsync` also runs `PurgeLegacyEmptyTurns`, so a session written before the lazy turn
messages existed is cleaned on the way in. The purge happens in memory only: the cleaned session is
persisted by the autosave pump, never by a synchronous full re-serialisation inside the load path.
A multi-megabyte `setItem` on the single WASM thread was one of the blocking points behind
`RenderProcessUnresponsive` (see [message-window.md](message-window.md)).

### `GetRecentSessionsAsync(count)`

Recent session summaries, cached in memory. Sessions without a user message are treated as invalid
and removed.

### `Options`

The active `ConnectionProfile`. A getter over `IProfileManager.ActiveProfile`.

## Invariants

| Invariant | Why |
|-----------|-----|
| A run never reads `Session` | a run must not follow the user into another session mid-flight |
| `ProcessStreamAsync` closes the message in `finally` | an open message vanishes from the wire and freezes in the UI |
| `GetTurnCompletionsAsync` executes no tools | the tool loop inside the gateway is what made between-turn mutations invisible |
| `TotalTokens` is assigned from provider usage, never accumulated | provider usage is the whole context, so adding double-counts |
| Compression events fire for every path | the invisible paths have no other UI signal |

## Test seams

Two `internal` properties let a test replace the transport without re-implementing the service:

- `ChatGateway` — the per-turn seam.
- `CompressionGateway` — the summary request.

Both default to the same `AfCompletionsAdapter`. Tests that observe a *request* set these; tests
that observe a *failure* keep the real adapter and point the profile at a fake endpoint
(`WireScriptedChatService`, `ScriptedChatService`, `RecordingChatService` in `UIBlazor.Tests`).
