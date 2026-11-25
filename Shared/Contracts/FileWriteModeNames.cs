namespace Shared.Contracts;

/// <summary>
/// The wire names of the <c>create_file</c> <c>mode</c> argument, normalised in ONE place.
/// <para>
/// This is a CONTRACT, not a UI convenience: the tool accepts aliases (<c>create</c> and
/// <c>replace</c> both mean <c>overwrite</c>), so anything that displays the mode must resolve
/// them exactly like the host does. A second copy of this table in the UI would drift from the
/// host silently — the badge would claim <c>append</c> while the host appended nothing, or worse,
/// claim <c>overwrite</c> for a value the host refuses.
/// </para>
/// <para>
/// The enum itself stays in <c>ToolCore</c> (<c>FileWriteMode</c>): <c>Shared</c> owns the names
/// that travel over the wire and inside the tool schema, <c>ToolCore</c> owns the behaviour.
/// </para>
/// </summary>
public static class FileWriteModeNames
{
    /// <summary>
    /// Every value <see cref="TryNormalize"/> accepts, aliases included. <see cref="IsPrefixOfAKnownMode"/>
    /// reads this list, so an alias missing here makes the guard blind to it: a mid-stream `" conc"`
    /// would be reported as a mode the host refuses instead of one still arriving.
    /// <para>
    /// Adding an alias therefore takes TWO edits in this file — a `case` in <see cref="TryNormalize"/> and
    /// a name here — and nothing enforces it. The alias literals are spelled a second time in the VS
    /// Code host (`parseMode`), which is the price of it being a separate language.
    /// </para>
    /// </summary>
    private static readonly string[] KnownValues = [Overwrite, Append, Prepend, "create", "replace"];

    /// <summary>Replace the whole file. Also what <c>null</c>/empty means.</summary>
    public const string Overwrite = "overwrite";

    /// <summary>Add the content to the end of the file.</summary>
    public const string Append = "append";

    /// <summary>Add the content to the start of the file.</summary>
    public const string Prepend = "prepend";

    /// <summary>
    /// Resolves the tool's <c>mode</c> argument to one of the three canonical names.
    /// Returns <c>false</c> for a value the host will refuse, and then <paramref name="normalized"/>
    /// carries the trimmed raw value so the caller can show what was actually asked for.
    /// </summary>
    public static bool TryNormalize(string? mode, out string normalized)
    {
        switch (mode?.Trim().ToLowerInvariant())
        {
            case null or "" or Overwrite or "create" or "replace":
                normalized = Overwrite;
                return true;
            case Append:
                normalized = Append;
                return true;
            case Prepend:
                normalized = Prepend;
                return true;
            default:
                normalized = mode.Trim();
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="raw"/> is a strict PREFIX of a value the host accepts — the shape a
    /// value has while it is still streaming in and the chunk boundary landed inside it.
    /// <para>
    /// The arguments are repaired to valid JSON at every chunk, so <c>"mode": "append"</c> arrives
    /// mid-stream as the perfectly legal <c>"app"</c>. Rendering that as an unknown mode claims the
    /// host refuses a call that was never broken, so a caller that knows the arguments are not final
    /// yet must show "still arriving" instead. An empty value is a prefix of everything, which is
    /// correct: the key has arrived but nothing of its value has.
    /// </para>
    /// </summary>
    public static bool IsPrefixOfAKnownMode(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        foreach (var known in KnownValues)
        {
            if (known.Length > text.Length && known.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
