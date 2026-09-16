namespace UIBlazor.Models;

/// <summary>
/// A single custom regex-based content filter rule.
/// </summary>
public class ContentFilterRule
{
    public string Name { get; set; } = string.Empty;

    public string Pattern { get; set; } = string.Empty;

    public string Replacement { get; set; } = string.Empty;

    /// <summary>
    /// Glob file pattern (e.g. "*.cs"). Empty means apply to all files.
    /// </summary>
    public string FilePattern { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;
}
