namespace UIBlazor.Services;

/// <summary>
/// Builds a <see cref="SolutionTreeEntry"/> tree from flat lists of file paths and project paths.
/// Infers directory structure from file paths and groups files under project nodes.
/// </summary>
public static class SolutionTreeBuilder
{
    /// <summary>
    /// Builds a solution tree from file paths and optional project paths.
    /// </summary>
    /// <param name="filePaths">Full paths to files in the solution.</param>
    /// <param name="projectPaths">Full paths to project files (e.g. .csproj). Optional.</param>
    /// <param name="solutionPath">Root path of the solution. If null, common root is computed.</param>
    /// <returns>A root <see cref="SolutionTreeEntry"/> whose children are the top-level tree nodes.</returns>
    public static SolutionTreeEntry Build(
        List<string>? filePaths,
        List<string>? projectPaths = null,
        string? solutionPath = null)
    {
        var root = SolutionTreeEntry.Directory(string.Empty);

        if (filePaths == null || filePaths.Count == 0)
            return root;

        // Normalize paths
        var normalizedFiles = filePaths
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Replace('\\', '/').TrimEnd('/'))
            .Distinct()
            .ToList();

        if (normalizedFiles.Count == 0)
            return root;

        // Determine the base path for relative display
        var basePath = solutionPath?.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrEmpty(basePath))
            basePath = ComputeCommonRoot(normalizedFiles);

        // Build project name → project directory path mapping
        var projectDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (projectPaths != null)
        {
            foreach (var pp in projectPaths)
            {
                if (string.IsNullOrWhiteSpace(pp)) continue;
                var normalized = pp.Replace('\\', '/').TrimEnd('/');
                var dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
                if (dir != null)
                {
                    var name = Path.GetFileNameWithoutExtension(normalized);
                    projectDirs[dir.TrimEnd('/')] = name;
                }
            }
        }

        // Insert each file into the tree
        foreach (var filePath in normalizedFiles)
        {
            InsertPath(root, filePath, basePath, projectDirs);
        }

        // Sort all levels: directories first, then files, alphabetically
        SortTree(root);

        return root;
    }

    /// <summary>
    /// Inserts a single file path into the tree under root, relative to basePath.
    /// </summary>
    private static void InsertPath(
        SolutionTreeEntry root,
        string filePath,
        string basePath,
        Dictionary<string, string> projectDirs)
    {
        // Make relative to basePath
        var relative = filePath;
        if (!string.IsNullOrEmpty(basePath) && filePath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            relative = filePath[(basePath.Length + 1)..];
        }
        else if (!string.IsNullOrEmpty(basePath) && filePath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            relative = filePath[basePath.Length..].TrimStart('/');
        }

        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        var current = root;
        var accumulatedPath = basePath;

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            accumulatedPath = string.IsNullOrEmpty(accumulatedPath) ? part : accumulatedPath + "/" + part;
            var isLast = i == parts.Length - 1;

            if (isLast)
            {
                // File node
                current.Children.Add(new SolutionTreeEntry
                {
                    Name = part,
                    FullPath = filePath,
                    IsDirectory = false
                });
            }
            else
            {
                // Directory node — check if it's a project directory
                var dirPath = accumulatedPath;
                var dirName = part;

                // If this directory matches a project, use the project name
                if (projectDirs.TryGetValue(dirPath, out var projectName))
                {
                    dirName = projectName;
                }

                // Find or create the directory node — O(1) via index
                current.DirectoryIndex ??= new Dictionary<string, SolutionTreeEntry>(StringComparer.OrdinalIgnoreCase);
                if (!current.DirectoryIndex.TryGetValue(dirName, out var existing))
                {
                    existing = new SolutionTreeEntry
                    {
                        Name = dirName,
                        FullPath = dirPath,
                        IsDirectory = true
                    };
                    current.Children.Add(existing);
                    current.DirectoryIndex[dirName] = existing;
                }

                current = existing;
            }
        }
    }

    /// <summary>
    /// Recursively sorts tree: directories first (alphabetically), then files (alphabetically).
    /// </summary>
    private static void SortTree(SolutionTreeEntry node)
    {
        if (node.Children.Count == 0) return;

        var sorted = node.Children
            .OrderBy(c => !c.IsDirectory) // directories first
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        node.Children.Clear();
        node.Children.AddRange(sorted);

        foreach (var child in node.Children)
        {
            SortTree(child);
        }
    }

    /// <summary>
    /// Computes the common root directory from a list of file paths.
    /// </summary>
    private static string ComputeCommonRoot(List<string> paths)
    {
        if (paths.Count == 0) return string.Empty;
        if (paths.Count == 1) return GetParentPath(paths[0]);

        var firstParts = paths[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var commonParts = new List<string>();

        for (var i = 0; i < firstParts.Length - 1; i++) // -1 because last part is filename
        {
            var part = firstParts[i];
            var allMatch = paths.All(p =>
            {
                var pParts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return i < pParts.Length - 1 && pParts[i].Equals(part, StringComparison.OrdinalIgnoreCase);
            });

            if (allMatch)
                commonParts.Add(part);
            else
                break;
        }

        return string.Join('/', commonParts);
    }

    private static string GetParentPath(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx > 0 ? path[..idx] : string.Empty;
    }
}
