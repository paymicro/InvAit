using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Shared.Contracts;

/// <summary>
/// Structured representation of a file's content returned by <c>read_files</c> tool.
/// Instead of embedding line numbers and markdown fences on the VS side,
/// the raw lines are sent as a JSON array so that the Blazor frontend can
/// apply content filtering (strip comments, etc.) on clean text before
/// adding line numbers for display / LLM consumption.
/// </summary>
public class FileContent
{
    /// <summary>Relative or absolute path as requested by the caller.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Raw file lines (without line-number prefixes).</summary>
    [JsonPropertyName("lines")]
    public List<string> Lines { get; set; } = [];

    /// <summary>
    /// Total line count of the original file (populated when the file was truncated).
    /// <c>null</c> when the entire file was read.
    /// </summary>
    [JsonPropertyName("totalLines")]
    public int? TotalLines { get; set; }

    /// <summary>
    /// 1-based start line of the returned slice (for pagination).
    /// <c>null</c> or <c>1</c> when reading from the beginning.
    /// </summary>
    [JsonPropertyName("startLine")]
    public int? StartLine { get; set; }

    /// <summary>Error message if the file could not be read.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
