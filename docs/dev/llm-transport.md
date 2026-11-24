# LLM transport: timeouts, retries, error classification

How one LLM request is bounded, when it is retried, and what the user sees when it fails.

## Timeout ownership

**`AfCompletionsAdapter` is the only owner.** The transport clients built by
`AfChatClientFactory.Create` get no timeout (the `httpTimeoutSec` argument stays `null`).

Bounding the request twice was a real bug: the outer CTS was an idle window while the transport CTS
covered the whole body-read, so a long answer with steady chunks was cut mid-generation
("Request timed out").

| Profile | Scope of the window |
|---------|--------------------|
| `Stream = true` | **Idle window.** Re-armed after every delivered chunk, so it bounds connect + first byte and then the gap between chunks — never the total generation time. |
| `Stream = false` | **Total budget.** One body-read is one request, so the window is the whole exchange. |

`CommonOptions.HttpTimeoutSec`, default **100 s**. `<= 0` disables the timeout — the same "zero
means unlimited" convention `TokensToCompress` uses. A zero-second CTS would fire before the
request left the machine and turn every call into a timeout error.

A burned window surfaces as `LlmApiException` (transient), so it spends the retry budget instead of
ending the run.

## Retry policy

Two independent implementations, same numbers:

| Where | Budget | Delays |
|-------|--------|--------|
| `AIChat.HandleErrorAsync` (main agent, one turn) | `CommonOptions.MaxRetries`, default **10** | `RetryHandler.GetRetryDelay`: 2, 5, 10, then 20 s |
| `AfSubAgentOrchestrator.RunBranchTurnAsync` (one branch, whole run) | same setting | same |

A turn that arrives EMPTY (no content, no reasoning, no tool call) spends the same budget — see
[An empty turn is retried too](#an-empty-turn-is-retried-too).

The branch budget belongs to the **run**, not the turn: a branch cannot get N retries per turn,
which would multiply both LLM calls and the tokens charged against `MaxTokensPerSubAgent`.

### What is retried

The budget is spent only on a failure that can plausibly succeed on a repeat. That decision lives
in `LlmApiException.IsRetryable`, derived from the HTTP status the transport records.

| Kind | Status | Retried | Why |
|------|--------|---------|-----|
| No status | `null` | yes | timeout, socket reset, an error event inside a successful stream — the cause is unknown |
| Success that still failed | `2xx` | yes | the request was accepted, the body was unusable: a broken relay |
| Provider failure | `5xx` | yes | the request was fine, the provider was not |
| Transient by definition | `408` `409` `425` `429` | yes | |
| Rejected request | other `4xx` | **no** | a wrong key, a missing model and a malformed payload answer identically every time |
| Anything that is not `LlmApiException` | — | **no** | a bug in our own code; the transport wraps everything it produces, so this is never a hiccup |

The last row is the highest-value line in the whole policy. Before it existed, `RunSingleTurnAsync`
caught `Exception` — everything — so a `NullReferenceException` burned ten attempts and nineteen
minutes before showing the same stack trace.

A permanent failure is reported on the first attempt, and its message omits the attempt counter
(`Error: ...` instead of `Error: ... [1/10]`): the counter would claim nine attempts that will never
happen. The error toast follows the same rule.

### An empty turn is retried too

The policy above classifies EXCEPTIONS. A turn can also fail without throwing: the stream delivers
nothing at all — no content, no reasoning, no tool call — and closes normally.

That is not hypothetical. A provider answering with an empty `delta` (every field `null`, often with
`completion_tokens: 0`) is dropped by `AfDeltaMapper.Map`, because it carries no payload; the
caller's stream then simply ends. `ProcessStreamAsync` closes the message, no exception is raised,
and before this rule the run was over:

| Loop | Before | Now |
|------|--------|-----|
| `AIChat.RunSingleTurnAsync` | the empty bubble was dropped and the method returned `null` — the run ended silently, the user's prompt stayed unanswered with no explanation | one attempt of the same budget; each retried attempt leaves a technical note (`1/3 in 2s — empty response`), and the deciding attempt leaves a terminal one |
| `AfSubAgentOrchestrator.RunBranchTurnAsync` | the turn never materialized, the loop saw "no tool calls" and broke out, the parent received `(Sub-agent returned an empty response.)` | the same `TrySpendRetryAttemptAsync` a thrown failure uses; when the budget is gone the branch ends `Completed` with the empty marker plus a `(No output after N attempt(s).)` note |

Four properties are load-bearing:

- **The budget is shared, not added.** An empty turn and a thrown failure draw from the SAME
  counter, so a branch cannot get N retries per turn. The billing argument is about CALLS, not
  tokens: `session.TotalTokens` is the context size rebased from the last request's usage, and a
  discarded attempt is removed from the session anyway.
- **Reasoning counts as payload.** A turn that produced only `reasoning_content` is NOT empty: a
  reasoning model legitimately answers that way, and retrying it would burn the whole budget on a
  success.
- **The empty attempt leaves no turn behind.** It never entered the branch history (the message is
  materialized on the first payload) or was removed before the retry (main agent), and the technical
  note is `IsTechnical` — invisible to `BuildWire`, to the compression plan and to the token
  estimate.
- **Whitespace is emptiness.** `HasPayload` uses `IsNullOrWhiteSpace`, the same rule
  `ConversationSession.IsEmptyAssistant` applies when it filters the wire. A turn that streamed only
  a newline must be retried, not accepted and then dropped from the next request.

#### There is no retryability signal for silence

An exception carries `StatusCode`, so a `401` fails at once. Silence carries nothing: the HTTP call
succeeded, the body was valid, the model simply produced no output. Silence is therefore
indistinguishable from a provider hiccup and spends the WHOLE budget — with the default of 10 that
is 137 s of backoff (`2+5+10+20×6`) for a deterministically silent model.

That is a deliberate choice, not an oversight: a shorter budget for this one class would need a
second constant next to `MaxRetries`, and the cost of guessing wrong is asymmetric — cutting a
genuine hiccup short loses the answer, while a long wait is visible and cancellable (the countdown
runs on the bubble, the Cancel button is available the whole time). Narrowing it belongs with the
per-class budgets in [Remaining work](#remaining-work), where the numbers are decided together.

#### The deciding attempt is not silent — and is not assistant speech

A retried attempt leaves a technical note. The FINAL one cannot be left out: it decides the
outcome, and a run that ends with a note trail and no conclusion still does not tell the user what
happened.

It is written as a NOTE too, and that is the whole point. The obvious shape — keep the last bubble
and write the diagnostic into it — is wrong, because that bubble is an ordinary assistant message:
`BuildWire` would keep it (it is not `IsTechnical`, not `IsStreaming`, not empty), so on the NEXT
turn the model would read our diagnostic as its own last utterance. Two amplifiers make it worse: an
interject typed during the countdown starts a new turn whose wire BEGINS with it, and
`PlanCompression` counts it as visible, so a summary can bake it into the context permanently.

| Loop | Terminal outcome | Where it lives |
|------|------------------|----------------|
| Main agent | `Empty response from the model — retry budget exhausted (max N).` | technical note — visible, never context |
| Branch | `(Sub-agent returned an empty response.)` + `(No output after N attempt(s).)` | `ResultText` / `run.NoteText`, i.e. text for the parent agent, not for the branch wire |

The branch ends `Completed`, not `Failed`: nothing errored, the model answered with nothing.

### The status must survive the wrappers

The whole policy reads ONE field — `StatusCode` — so anything that rebuilds an exception between
the transport and the caller silently disables it. A rebuilt exception has `StatusCode = null`,
which the table above reads as "cause unknown" and therefore as retryable. A `401` then costs ten
attempts and nineteen minutes, which is precisely the defect the classification was introduced to
remove.

Two wrappers sit on that path, and both must let an already-classified exception through untouched —
not rebuild it, not even to "preserve the inner exception":

| Wrapper | Path it covers |
|---------|----------------|
| `AfCompletionsAdapter.AcquireUpdatesAsync` | the non-streaming acquire — a `4xx` arrives here as a thrown `LlmApiException` |
| `AfCancelRace.GuardAsync` | every streaming `MoveNext` — the status is thrown on the FIRST move, so it always passes through this wrapper |

Both use the catch filter `ex is not OperationCanceledException and not LlmApiException`. The
`LlmApiException` exclusion is load-bearing, not cosmetic: without it a `401` reaches the caller as
`null`. `LlmApiStatusPropagationTests` pins both paths and fails with `Actual: null` if either
filter loses the exclusion.

### Why the number 10 is not the problem

Ten attempts for a flapping relay or a transient `5xx` is defensible; it is exactly what the budget
is for. The defect was that the same budget was spent on permanent failures, which is what the
classification fixes. It does **not** narrow the window for genuinely transient trouble.

### Known gap: provider rate limits

`429` is retried, but with blind backoff: the codebase handles no `Retry-After`,
`x-ratelimit-reset-*` or any other limit header. The provider states how long to wait and the code
does not listen, so three attempts inside a one-minute window hit an endpoint that is already
refusing.

This is a **deliberate** omission, not an oversight: provider-specific limit behaviour is left to
the user. If it is ever implemented, see [Remaining work](#remaining-work) — §2 is written and
ready, and §3/§5 become meaningful only then.

### Cost of a retry is not only time

A stream that dies after 500 generated tokens loses them and pays for them again; no provider
offers "resume from here". A retry therefore costs money, which is the argument for keeping the
budget small for the most expensive class (`Timeout` — each attempt already costs a full window).

## Remaining work

Implemented: error classification (§1) and the technical-note trace. The rest is a candidate list,
not a plan — each item is independent and can ship alone.

### 1. Error classification — **DONE**

`LlmApiException.StatusCode` + `IsRetryable`, filled by the transport at every throw site (see
[What is retried](#what-is-retried)).

`LlmErrorKind` as a separate enum was considered and dropped: the status already carries the
information, and a second representation would have to be kept in sync with it. A bug in that sync
would silently change what gets retried — the one thing this code must not get wrong quietly.

### 2. `Retry-After` for rate limits — **deliberately not planned**

Provider-side limit behaviour is left to the user: it was considered once, never implemented, and
stays a feature request rather than a defect. `429` is already classified retryable (§1), so a
provider that recovers on its own needs no header parsing.

If it is ever implemented: honour the header with a ceiling (so a broken header cannot freeze the UI
for an hour), and when absent fall back to a **60 s ceiling** instead of 20 s — three attempts
inside a one-minute window with a 20 s cap are three wasted hits on an endpoint that is already
refusing.

### 3. Budgets per class — candidate

One counter for everything is the remaining imbalance. `429` and `Timeout` share the attempt count
of a cheap `5xx`, although one attempt of each costs wildly different amounts of time.

| Kind | Budget | Rationale |
|------|--------|-----------|
| `Transport` / `5xx` | 10 attempts (current) | cheap, and it genuinely recovers |
| `RateLimited` | **time-based**, e.g. 5 min of total waiting | a limit lives for minutes; an attempt counter is meaningless |
| `Timeout` | 2–3 attempts | each one already costs a full window |
| `Permanent` | 0 (done in §1) | |

A weighted single counter ("this attempt costs 2×") is worse: *how long I waited* and *how many
times I tried* are different quantities, and merging them makes behaviour unpredictable again.

### 4. Jitter — candidate

`RetryHandler.GetRetryDelay` is deterministic. Parallel branches fail together (one server) and then
retry in lockstep, re-creating the burst that caused the failure. ±20 % would break the lockstep.
This is the cheapest remaining item: two lines, no behaviour change for a single run.

### 5. No retry storm across branches — depends on §3

A batch of N branches must not produce N × 10 retries against the same endpoint. Jitter (§4) spreads
them, but the real fix is the time-based budget in §3: branches would then share wall-clock time
instead of each holding its own attempt counter.

## UI trace of a failed turn

A retried turn used to vanish without a trace: `HandleErrorAsync` removed the partial message and
the loop started over with a new one, so the only evidence lived in the log.

Now each retry leaves a **technical message** in the history — a compact, non-LLM line:

```
Retry 2/10 in 5s — HttpCode: 429 | rate limit
```

Properties:

- **Also for a retried EMPTY turn** (`1/3 in 2s — empty response`), not only for a thrown failure: an
  empty answer is retried on the same budget ([above](#an-empty-turn-is-retried-too)). A cancelled
  countdown leaves no note — the note is written AFTER the wait.
- **Never reaches the model.** `VisualChatMessage.IsTechnical` is filtered out by `BuildWire`, by
  `PlanCompression` and by the token estimate. The model must not pay for our transport noise, and a
  "timeout" line in the context would invite the model to comment on it.
- **Persisted with the session.** After a reload the user can still see that a session had six
  timeouts, which is exactly the context needed to explain a slow run.
- **Not editable, not regenerable, not countable as a turn.** It is a log line that happens to live
  in the transcript.
- **Styled as the archive plaque, not as a bubble.** Both the retry note and the hidden-middle
  plaque are dividers, so both render on the same `.divider-row*` classes (`wwwroot/css/app.css`):
  a title (`RetryNoteTitle`), the text, and a right-hand action slot. The dismiss button lives in
  that slot - the same one the plaque's "show earlier" button uses - rather than in the shared
  message action bar, which is suppressed for technical notes.

The terminal failure is different: the last attempt keeps its assistant message with the error
text, because at that point the error *is* the answer to the user's request.

## Related

- Who drives the retry: [agent-turn-loop.md](agent-turn-loop.md#traps)
- `LlmApiException` in context: [chat-service.md](chat-service.md)
