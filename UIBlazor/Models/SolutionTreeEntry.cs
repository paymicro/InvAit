using System.Text.Json.Serialization;

namespace UIBlazor.Models;

/// <summary>
/// Represents a node in the solution tree structure.
/// Files have <see cref="FileSize"/> in bytes; directories have <see cref="Children"/>.
/// </summary>
public class SolutionTreeEntry
{
    /// <summary>Display name (relative, e.g. "Program.cs" or "Agent").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Full path on disk (used for size lookup, may be null in tests).</summary>
    public string? FullPath { get; set; }

    /// <summary>True for directories, false for files.</summary>
    public bool IsDirectory { get; set; }

    /// <summary>File size in bytes, or null for directories / unknown.</summary>
    public long? FileSize { get; set; }

    /// <summary>Child entries (directories and nested files).</summary>
    public List<SolutionTreeEntry> Children { get; set; } = [];

    /// <summary>
    /// Internal lookup index for directory children (used by SolutionTreeBuilder for O(1) lookup).
    /// </summary>
    [JsonIgnore]
    internal Dictionary<string, SolutionTreeEntry>? DirectoryIndex { get; set; }

    public static SolutionTreeEntry Directory(string name, string? fullPath = null, List<SolutionTreeEntry>? children = null) =>
        new() { Name = name, FullPath = fullPath, IsDirectory = true, Children = children ?? [] };

    public static SolutionTreeEntry File(string name, long? size = null, string? fullPath = null) =>
        new() { Name = name, FullPath = fullPath, IsDirectory = false, FileSize = size };
}
