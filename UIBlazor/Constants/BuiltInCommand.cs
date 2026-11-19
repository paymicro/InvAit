namespace UIBlazor.Constants;

/// <summary>
/// Names of the built-in slash commands of the chat input.
/// <para>
/// The command list and the dispatcher used to agree on a bare string literal, so renaming a
/// command meant grepping for it in two places and the compiler could not help. One constant per
/// command keeps the suggestion menu, the dispatcher and the components that trigger a command
/// (the archive plaque, the compress hint) in sync.
/// </para>
/// <para>
/// The name is <c>compress</c>, not <c>compact</c>: everything the user reads - the badge, the
/// notification, the plaque button, the settings label - already said "compress", while a second
/// word for the same operation lived only in the command line and in code comments.
/// </para>
/// </summary>
public static class BuiltInCommand
{
    /// <summary>
    /// Summarises the session so the context shrinks. The only built-in command today; it is the
    /// same operation the mid-run gate and the archive plaque run.
    /// </summary>
    public const string Compress = "compress";
}
