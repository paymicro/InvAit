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

- **Never reaches the model.** `VisualChatMessage.IsTechnical` is filtered out by `BuildWire`, by
  `PlanCompression` and by the token estimate. The model must not pay for our transport noise, and a
  "timeout" line in the context would invite the model to comment on it.
- **Persisted with the session.** After a reload the user can still see that a session had six
  timeouts, which is exactly the context needed to explain a slow run.
- **Not editable, not regenerable, not countable as a turn.** It is a log line that happens to live
  in the transcript.

The terminal failure is different: the last attempt keeps its assistant message with the error
text, because at that point the error *is* the answer to the user's request.

## Related

- Who drives the retry: [agent-turn-loop.md](agent-turn-loop.md#traps)
- `LlmApiException` in context: [chat-service.md](chat-service.md)
