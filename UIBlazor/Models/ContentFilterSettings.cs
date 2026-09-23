namespace UIBlazor.Models;

/// <summary>
/// Content filter settings stored in <see cref="CommonOptions"/>.
/// Controls stripping/minimization of tool result content to reduce token usage.
/// </summary>
public class ContentFilterSettings
{
    public bool IsEnabled { get; set; }

    public bool StripComments { get; set; } = true;

    public bool StripBlankLines { get; set; } = true;

    public bool TrimWhitespace { get; set; } = true;

    public List<ContentFilterRule> CustomRules { get; set; } = [];
}
