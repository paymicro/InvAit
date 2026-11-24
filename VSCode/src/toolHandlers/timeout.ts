/**
 * The `timeoutMs` channel rule shared by BOTH Node entry points: the bash/git handlers and
 * the MCP arm of `toolDispatcher`. Mirror of `ToolTimeout.Resolve` in
 * `Shared/Contracts/ToolTimeout.cs` (the Node host shares no code with the C# one, so the
 * rule and the defaults are duplicated there with a pointer back to `ToolTimeout`).
 *
 * Rule: absent or non-positive means "the tool default" — NEVER "no limit" and NEVER
 * "kill instantly". The trap is platform-specific: C# would kill immediately on
 * `Task.Delay(0)`, while `spawnSync({ timeout: 0 })` removes the limit entirely, so the
 * same field would mean opposite things on the two hosts.
 *
 * Parsing is deliberately STRICT (whole number only), matching `int.TryParse` on the C#
 * side: `parseInt` reads "30s" as 30 and "2 minutes" as 2, i.e. invents a 2 ms limit out
 * of prose.
 */

/** Largest value C# `int.TryParse` accepts — and the largest delay a JS timer can hold. */
export const MAX_TIMER_MS = 2_147_483_647;

/**
 * Smallest limit the MCP channel can honour. `McpClientRegistry` clamps with
 * `Math.max(1000, …)` before building its timer; resolving to the same floor keeps the
 * number that KILLS the call equal to the number the caller and the model are told about.
 * The bash channel has no such clamp, so it keeps the value it was given.
 */
export const MIN_MCP_TIMER_MS = 1_000;

/**
 * Parses a whole positive number within the `int` range, or returns null when the value
 * must fall back.
 *
 * - `Number.isSafeInteger` (not `isFinite`): `1e21` is an integer for JS but cannot be
 *   represented exactly and would make `spawnSync` throw `RangeError`.
 * - The upper bound is `MAX_TIMER_MS`, not a clamp: `int.TryParse("3000000000")` FAILS on
 *   the C# host, so it falls back to the tool default. Clamping here instead would give the
 *   same payload two different limits on the two hosts — the exact drift this file exists
 *   to prevent.
 * - A leading `+` is accepted because `int.TryParse("+45000")` accepts it.
 */
function parseWholeMs(raw: unknown): number | null {
    let value: number;

    if (typeof raw === 'number') {
        value = raw;
    } else if (typeof raw === 'string' && /^\+?\d+$/.test(raw.trim())) {
        value = Number.parseInt(raw.trim(), 10);
    } else {
        return null;
    }

    return Number.isSafeInteger(value) && value > 0 && value <= MAX_TIMER_MS ? value : null;
}

/**
 * Resolves the effective limit for a tool call.
 *
 * @param raw        the `timeoutMs` value as it arrived in the payload (untrusted).
 * @param fallbackMs the default of THIS tool (bash 120 s, MCP 600 s) — see `ToolTimeout`.
 * @param minMs      the smallest value the receiving channel can honour (see
 *                   {@link MIN_MCP_TIMER_MS}); the default keeps the value unchanged.
 */
export function resolveTimeoutMs(raw: unknown, fallbackMs: number, minMs = 1): number {
    const parsed = parseWholeMs(raw);
    if (parsed === null) {
        return fallbackMs;
    }

    return Math.max(minMs, parsed);
}
