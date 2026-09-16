namespace UIBlazor.Services.Interfaces;

/// <summary>
/// Provides content filtering/minimization for tool results to reduce token usage.
/// Reads configuration from <see cref="ICommonSettingsProvider"/>.
/// </summary>
public interface IContentFilter
{
    /// <summary>
    /// True when the content filter is enabled and has at least one active transformation.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Applies the configured filters to the given content produced by the specified tool.
    /// </summary>
    /// <param name="content">The raw tool result content to filter.</param>
    /// <param name="toolName">The name of the tool that produced the content (e.g. "read_files", "grep", "bash").</param>
    /// <returns>The filtered content, or the original content if filtering is disabled.</returns>
    string Filter(string content, string toolName);
}
