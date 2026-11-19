# Developer documentation

Deep documentation of the parts of `UIBlazor` that are hard to reason about from a single file:
the agent turn loop, the LLM transport (timeouts, retries, error classification), the session
model and the chat service contract.

`AGENTS.md` carries the architecture map and the rules that must not be broken. These documents
carry the *why*: the invariants, the failure modes they prevent, and the traps that produced them.

> `docs/` itself holds the screenshots used by the root `README.md` (`/docs/*.png`). This page and
> its siblings keep developer notes apart from user-facing assets.

| Document | Answers |
|----------|---------|
| [agent-turn-loop.md](agent-turn-loop.md) | Who owns the LLM turn cycle, what happens between turns, why the loop left the gateway |
| [llm-transport.md](llm-transport.md) | Timeout ownership, retry policy, error classification, the UI trace of a failed turn |
| [session-and-context.md](session-and-context.md) | `BuildWire`, compression planning, the system-prompt cache, token accounting |
| [chat-service.md](chat-service.md) | `IChatService` surface: what each member guarantees and who may call it |

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

A change needs a documentation edit when it touches:

| Change | Document |
|--------|----------|
| Turn-loop shape, who calls `GetTurnCompletionsAsync`, between-turn mutations | [agent-turn-loop.md](agent-turn-loop.md) |
| Timeout ownership, retry budget, error classification, `LlmApiException` | [llm-transport.md](llm-transport.md) |
| `BuildWire` validation rules, compression planning, prompt cache key | [session-and-context.md](session-and-context.md) |
| An `IChatService` member (add/remove/change a guarantee) | [chat-service.md](chat-service.md) |
