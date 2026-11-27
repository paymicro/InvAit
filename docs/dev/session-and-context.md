# Session and context

The session model, the one function that turns it into a request, and the caches that keep a long
run affordable.

## The session is the only history

`ConversationSession` holds `List<VisualChatMessage>`. There is no second, wire-shaped history
anywhere in the application — see [agent-turn-loop.md](agent-turn-loop.md) for why that is the
design and not an accident.

`Revision` is a monotonic counter bumped by every structural mutation (`AddMessage`,
`RemoveMessage`, `SetMessages`, `CollapseMessages`, `UpdateMessage`). Callers use it to detect "the
user changed something between two turns" without diffing the list. It is a plain increment under
`_messagesLock`, not an atomic operation — all mutations are already serialised by that lock.

## `BuildWire` — the single entry point

`BuildWire(systemPrompt, contentFilter, live)` returns `(List<WireMessage>, VisualChatMessage?)`:
the request history and the last surviving user message.

It is the **only** place a request history is assembled, and it validates as it builds, because
providers reject shapes that look fine to us:

| Rule | Failure it prevents |
|------|---------------------|
| No orphaned `tool_calls` — every call without a `Result` gets a stub | `assistant(tool_calls)` with no matching `tool` message → hard `400` |
| No empty assistant messages (no text, no reasoning, no calls) | providers reject an empty turn |
| Streaming messages never leak in, wherever they sit | a live turn would be sent as if complete |
| Dedupe by **reference**, first wins | the same object added twice (mirror attach, overlapping snapshots) |
| The caller's own `live` message is excluded by reference | a turn must not see itself |

`Id` is **not** used for deduplication: `VisualChatMessage.Id` is `[JsonIgnore]` and generated in
the constructor, so every message loaded from storage has a new one.

One thing `BuildWire` does NOT decide: which field of a result becomes the text of a `tool` message.
It sends `ToolCall.Result.Content`, and `ToolResult.Convert` chose that string when the result was
constructed — `Result` unless it carries nothing (empty or whitespace), `ErrorMessage` only then
([agent-turn-loop.md](agent-turn-loop.md#what-the-parent-reads-back)).

`BuildWire` mutates the session objects when it stubs an orphan call. That is deliberate: the UI
must show the same "not executed" result the model sees. The mutation is idempotent — a call that
already has a `Result` is never touched.

A tool call with an empty `Id` cannot be stubbed: `PrepareMessages` cannot pair it with a `tool`
message. It stays orphaned. Only the source can fix it (every real API supplies an id).

## Compression

`CompressSessionAsync(session, capture, ct)` collapses a slice of the history into one LLM summary.
The shape is `[head window][summary][tail window]`: everything outside the collapsed slice stays
verbatim.

`PlanCompression` protects four things:

1. **Visible messages only** — an assistant turn without payload can never occupy a window slot.
2. **Live messages of the running turn are never collapsed** — the turn loop keeps streaming into
   those exact objects; removing them orphans the stream and the UI freezes.
3. **A short history is not compressed.** Under eight visible messages there is no middle to
   replace, and a summary would only add noise. The gate then declines, which is a legitimate
   outcome and not a bug.
4. **The newest user message is not forced into the tail.** In a long tool-heavy run the task
   message is far behind the last turns; extending the tail back to it would empty the collapsed
   slice, so the gate would never compress the very run it exists for.

The collapse happens **in place** (`CollapseMessages`): only the planned slice is replaced by the
summary object. Every other message keeps its identity. A whole-list replacement used to orphan the
live streaming message.

After a successful compression `TotalTokens` is re-armed from
`EstimateContextTokens(messages, tools)`. Without the re-arm the mid-run gate sees "still over the
limit" at every turn boundary and re-summarises the already compressed history — this was one of the
original bugs behind "compression fires constantly".

`EstimateContextTokens` counts message text, tool-call arguments, tool results **and the serialised
tool schemas**. The schemas ride along with every request (a strict-mode toolset is thousands of
tokens of constant overhead), and they were missing from the estimate, so the re-armed value was
systematically lower than the provider usage it replaces.

## Token accounting

Two different numbers, deliberately:

| Number | Meaning | Written by |
|--------|---------|------------|
| `session.TotalTokens` | **Context position** — what the next request will cost | `AfUsageHelpers.CaptureUsage` (provider usage), `EstimateTokens` (heuristic while streaming), `EstimateContextTokens` (after compression) |
| `message.Timings.Tokens` | **Generation size** — the badge, reasoning included | `ChatService.CalcTimings` |

Reasoning tokens are excluded from `TotalTokens` (they are not resent) and included in the badge
(the user asked how much was generated).

`UsageInfo.TotalTokens` from the provider is the whole context, so `CaptureUsage` **assigns**
`session.TotalTokens` rather than accumulating. Any code that adds to it would double-count.

## System prompt cache

`SystemPromptBuilder.PrepareSystemPromptAsync` caches its result by
`(Mode, CanDelegate, Revision)`.

The turn loop rebuilds the request on every turn, so without the cache a 40-turn run assembles the
prompt — with its skills scan, rules, `agents.md` and the full active-file content — 40 times. Worse
than the cost: any byte-level difference between two consecutive builds breaks the provider-side
prompt cache, so every turn is billed and processed as a cold prefix.

| Key part | Invalidated by |
|----------|----------------|
| `Mode` | `switch_mode` |
| `CanDelegate` | `delegate_task` toggled — changes the prompt without touching profile or context |
| `Revision` | IDE context push (`OnContextChanged`), profile edit (`PropertyChanged`) |

The cached entry is one immutable `(Key, Text)` record, so a concurrent refresh cannot pair a fresh
key with a stale text: a single reference read/write is atomic and the worst case is a redundant
rebuild.

`ISystemPromptBuilder.InvalidatePromptCache()` exists as an explicit seam for inputs that change
without an event (a skills or rules file edited on disk).

## Empty assistant turns

Turns are created differently in the two loops, and the difference is deliberate:

| Loop | When the bubble is added | Why |
|------|--------------------------|-----|
| Main agent (`AIChat.RunSingleTurnAsync`) | **eagerly**, before the first token | the bubble IS the feedback that the run started, and the only place the Cancel button and the retry countdown can live |
| Sub-agent branch (`AfSubAgentOrchestrator.RunBranchAsync`) | **lazily**, on the first payload (`Materialize`) | no Cancel button and no countdown live there, so a placeholder has nothing to show |

The two agree on the invariant that matters: an empty assistant turn never reaches the wire AND
never stays on screen. The main agent removes its bubble before a retry, the branch never adds it,
and `CollectVisibleForWire` drops any empty assistant message regardless of who created it. An
empty turn is otherwise an ATTEMPT, not an outcome — it spends the retry budget
([llm-transport.md](llm-transport.md#an-empty-turn-is-retried-too)).

Older builds created a placeholder at every turn start and kept it. `PurgeLegacyEmptyTurns()` drops
those on load, so sessions written by them neither render frozen bubbles nor send them to the model.
The predicate is content-based (`ConversationSession.IsEmptyAssistant`): assistant, no text, no
reasoning, no tool calls — the same rule `HasPayload` uses to decide whether a turn is empty. There
is no separate flag: the retired `VisualChatMessage.IsEmptyTurn` was deleted because no release ever
shipped with it.
