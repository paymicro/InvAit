# Tool timeout

Who limits how long a tool may run, which number reaches which layer, and why the client must wait
longer than the host kills.

Owner of this topic: any change to `CommonOptions.ToolTimeoutMs`, to the `timeoutMs` payload field,
to `ToolTimeout`, or to a `ProcessExecutor` limit belongs here in the SAME change.

## The three clocks

Three independent clocks bound one tool call, and only two of them are visible to the user:

| Clock | Where | What it bounds |
|-------|-------|----------------|
| **Client** | `VsBridge.SendRequestAsync` | how long the UI waits for the host's answer |
| **Host** | `ToolExecutor` → `ProcessExecutor` | when the process tree is killed |
| **Process** | `Task.Delay` / `spawnSync({ timeout })` | the actual OS-level kill |

Before this design only the client clock was driven by `CommonOptions.ToolTimeoutMs`
(`SettingsDialog` slider). The other two were the literal `120_000` in
`InvAit/Agent/ToolExecutor.cs` and `VsCode/src/toolHandlers/bash.ts`. The setting was therefore not
a limit at all — it was a waiting time, and it disagreed with the real limit in BOTH directions:

| Setting | What happened |
|---------|---------------|
| `< 120 s` | the client gave up first: the user saw a timeout, the command kept running, and its output was read by nobody |
| `> 120 s` | the host killed the command on the second minute: long builds/tests failed although the user allowed more |

## Ownership

| Layer | File | Rule |
|-------|------|------|
| One rule, one place | `Shared/Contracts/ToolTimeout.cs` | `Resolve(requested, fallback)` and `ClientWait(setting, hostExtra, fallback)` |
| UI setting | `UIBlazor/Options/CommonOptions.cs` | `ToolTimeoutMs`, default `ToolTimeout.DefaultCommandMs` |
| Payload — command tools | `UIBlazor/Agents/BuiltInAgent.cs` (`WithToolTimeout`) | `bash`, `git_status`, `git_log` args get `timeoutMs` from the setting |
| Payload — MCP | `UIBlazor/Services/Settings/ToolManager.cs` | the same setting is written into the `mcp__{server}__{tool}` args |
| Client wait | `UIBlazor/VS/VsBridge.cs` (`ComputeWait`) | host chain + `ToolTimeout.ClientGraceMs`, MCP also `+ McpHostGraceMs` |
| VS host | `InvAit/Agent/ToolExecutor.cs` | reads `timeoutMs`, hands it to `ProcessExecutor`; the git tools forward it |
| Process | `ToolCore/ProcessExecutor.cs` | normalizes, then `Task.Delay(timeoutMs)` |
| VS Code host — command | `VsCode/src/toolHandlers/bash.ts` | reads `timeoutMs`, `spawnSync({ timeout })`; `git_status`/`git_log`/`git_diff` forward it |
| VS Code host — rule | `VsCode/src/toolHandlers/timeout.ts` | the Node copy of `Resolve`, shared by bash and MCP so a fix cannot land in one channel only |
| VS Code host — MCP | `VsCode/src/toolDispatcher.ts` (`resolveMcpTimeoutMs`) | the same channel rule, MCP fallback 600 s |

Both hosts receive the value from ONE place — the UI setting — so the defaults below describe only
what a tool falls back to when the field is missing, i.e. when the host is called directly:

| Tool | Fallback | Why this number |
|------|----------|-----------------|
| `bash` | `DefaultCommandMs` = 120 s | an interactive shell command; two minutes is already generous |
| MCP `call_tool` | `DefaultMcpCallMs` = 600 s | a direct MCP call may be a heavy remote operation (indexing, search), so its unguarded default is the largest. In the product the field is always present, so the effective MCP limit is the setting — also 120 s by default |
| `ProcessExecutor.ExecuteAsync` | `DefaultExecuteMs` = 30 s | direct executable run, not a user command |

## The channel rule

`timeoutMs` is the SAME field in both channels, so it must mean the same thing in both hosts:

> **Absent or non-positive → the tool default.** Never "no limit", never "kill instantly".

- `ToolTimeout.Resolve` implements it for C#.
- `resolveTimeoutMs` in `VsCode/src/toolHandlers/timeout.ts` mirrors it by hand — the Node host shares
  no code with the C# one, so the constant and the rule are duplicated there with a comment pointing
  at `ToolTimeout`. Both Node entry points (`bash.ts` and the MCP arm of `toolDispatcher.ts`) import
  that ONE function; keeping two copies was how a fix would have reached a single channel.
- The trap that forced the rule: `Task.Delay(0)` fires immediately (the process dies before it prints
  anything), while `spawnSync({ timeout: 0 })` REMOVES the limit. The same `0` meant opposite things
  on the two hosts, and neither was "use the default".

Several numeric edges are traps on the Node side and are handled in that same helper. The
out-of-range ones are resolved the SAME way as the C# host does it — by falling back to the tool
default, not by clamping: clamping would give the same payload two different limits and resurrect
the drift this design kills.

| Edge | What happens without the guard | Resolution |
|------|--------------------------------|------------|
| `> 2^31-1` | `int.TryParse` fails in C#; in Node `setTimeout` stores the delay as a 32-bit int, so the timer fires after a few milliseconds while the message claims a huge limit — an instant kill | fall back to the tool default on BOTH hosts |
| `> 2^53` (e.g. `1e21`) | not exactly representable, `spawnSync` throws `RangeError` | fall back to the tool default (`Number.isSafeInteger`) |
| `< 1000` on the MCP channel | the registry rewrites it to 1 s, so the limit differs from the one the client and the model were told about | `Math.max(1_000, …)` — the registry's own floor |
| `+45000` / `" 45000 "` | accepted by `int.TryParse` | accepted |
| `"30s"`, `"2 minutes"`, `"4.5"` | `parseInt` invents 30 / 2 / 4 ms out of prose | rejected, tool default |

`ClientGraceMs` = 30 s is the second half of the rule: **the client waits longer than the WHOLE
host chain**, not longer than its last link.

```
bash:  client = setting            + ClientGrace
MCP:   client = setting + McpHost  + ClientGrace
```

Killing the process tree, draining the remaining stdout/stderr and the WebView2 round trip all take
time, so a client that waits exactly `setting` regularly wins the race and replaces the host's honest
`Command timed out after N ms` with its own `Transport failure: Request timed out`. The model then
believes the tool never ran.

The MCP channel needs the extra `McpHostGraceMs` because its host chain is longer: `ToolExecutor`
waits `timeoutMs + McpHostGraceMs` for the McpHost process, which owns the MCP server. Giving the MCP
request only the bash client wait would not fix the race — it would just move it into that channel.

`McpHostGraceMs` therefore appears in TWO clocks, and that is deliberate: the host spends it waiting
for McpHost to report the timeout, and the client spends it waiting for the host. Collapsing the two
would put the client back on the losing side of the race.

The sum is calculated in `long` and saturated at `int.MaxValue`. In `int` a setting near the maximum
overflowed into a NEGATIVE `TimeSpan`, and `CancellationTokenSource` rejects a negative delay with
`ArgumentOutOfRangeException` — every tool call would throw. The setting is loaded from
localStorage, not from the slider, so its range is not guaranteed by the markup.

## Why the setting is injected in the tool delegate

`BuiltInAgent` copies the model's arguments into a new dictionary and sets `timeoutMs` AFTER the copy,
so a `timeoutMs` that leaked out of the model's own arguments is overwritten: the timeout is the
user's decision, not the model's.

The injection lives in the `ExecuteAsync` delegate because that string exists only until the request
is sent. Mutating `ToolCall.Function.Arguments` instead would put the field into the transcript —
`ConversationSession.BuildWire` sends the call arguments back to the model — and into the rendered
tool-call block, where `timeoutMs` would appear as something the model wrote.

## What the model sees

A killed command is a normal failed tool result: `Success = false` with
`Command timed out after {timeoutMs}ms.` appended to the output (the number printed is the limit that
actually fired — a constant there would lie about the cause). It is NOT a transport error: the domain
refusal stays a result, per the MCP `CallToolResult { content, isError }` shape the bridge follows.

Both hosts keep the partial output, because the log of a long build is the most expensive thing to
lose: the VS host returns stdout+stderr plus the note, the Node host joins stdout, stderr and the note
and skips the empty ones. Only the note is guaranteed to be there.

On the Node host the note is NOT the only way a command can be killed, and conflating the two lies
about the reason exactly as the old constant did:

| `error.code` | Reported |
|--------------|----------|
| `ETIMEDOUT` | `Command timed out after N ms.` |
| `ENOBUFS` | output exceeded the buffer, killed — NOT a timeout |

The old check was `signal === 'SIGTERM'`, and an output overflow ALSO arrives with `SIGTERM`; only
the code differs. Classifying on the signal alone told the model a too-verbose command had timed out.

## The badge is display only

`ToolTimeoutBadge` reads the same setting, but it is NOT a limit: it draws a countdown from
`ExecutionStartedAtUtc` and shows the measured duration when the call finishes. A non-positive
setting hides the running countdown there — while the command itself still gets the tool default
(`ToolTimeout.Resolve`). Two different questions, two different answers on purpose: the badge answers
"how much longer may this take", the limit answers "when do we kill it".

## Accepted limitation

If the client's own wait expires (transport dead, JS bridge broken), the host's `ProcessExecutor`
keeps the process alive: the request is already forgotten on the UI side, and nothing cancels it —
`ProcessExecutor` runs in `InvAit` with no per-call token from the UI. A field in the payload cannot
fix that; it needs per-call cancellation (`todo.md`, the "Отмена тулзы в бейдже таймаута" item),
which is why that item is a prerequisite for a real cancel button, not a UI task.

The two hosts also differ in REACH, not in limit: the C# `ProcessExecutor` kills the whole process
tree, while `spawnSync` kills only the direct child. A wrapped build (`sh -c` → `dotnet build`) can
therefore outlive its timeout in VS Code and keep the CPU busy. Closing that needs explicit
process-group handling, so it is documented rather than pretended away.

## Test seams

- `ToolCore.Tests/ProcessExecutorTests`: `timeoutMs: 0` still succeeds (fallback, not instant kill);
  the timeout message prints the effective limit.
- `UIBlazor.Tests/VS/VsBridgeTests`: `ComputeWait` is strictly greater than the setting (and than the
  whole MCP chain), falls back on a non-positive one, and saturates instead of overflowing into a
  negative wait on a value near `int.MaxValue` — no waiting, so no flakiness.
- `UIBlazor.Tests/Agents/BuiltInAgentTests`: the setting rides in the payload and overrides the value
  the model supplied.
- `VsCode/src/test/toolHandlers/bash.test.ts`: `timeoutMs` reaches `spawnSync`, falls back on a
  missing, non-positive, prose or unrepresentable value, is clamped to the timer range, the message
  reports the effective number, the partial output survives the kill, an `ENOBUFS` overflow is not
  called a timeout, and all three git tools forward the field.
- `VsCode/src/test/toolDispatcher.test.ts`: the MCP channel forwards `timeoutMs`, applies the same
  non-positive fallback and the same range/floor clamps.
- `UIBlazor.Tests/Services/Settings/ToolManagerMcpTests`: the setting rides in the MCP args as well.
