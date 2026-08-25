namespace ToolCore.Tests;

/// <summary>
/// Resolves test asset executables inside their own project output directories so that
/// each asset runs against its own self-consistent .deps.json instead of the mixed
/// ToolCore.Tests bin folder.
/// </summary>
internal static class TestAssetLocator
{
    public static string GetAssetExePath(string assetProjectDirName, string exeName)
    {
        var path = GetProjectOutputPath(Path.Combine("TestAssets", assetProjectDirName, "bin", GetConfiguration(), "net10.0", exeName));

        // На Unix apphost собирается без расширения .exe
        if (!OperatingSystem.IsWindows() && !File.Exists(path))
        {
            var withoutExtension = Path.ChangeExtension(path, null);
            if (File.Exists(withoutExtension))
            {
                return withoutExtension;
            }
        }

        return path;
    }

    public static string GetHostDllPath()
        => GetProjectOutputPath(Path.Combine("..", "McpHost", "bin", GetConfiguration(), "net10.0", "InvAit.McpHost.dll"));

    private static string GetProjectOutputPath(string relativePath)
    {
        return Path.GetFullPath(Path.Combine(FindTestsRoot().FullName, relativePath));
    }

    private static string GetConfiguration()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    private static DirectoryInfo FindTestsRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && current.Name != "ToolCore.Tests")
            current = current.Parent;

        if (current == null)
            throw new InvalidOperationException("Could not locate ToolCore.Tests directory from " + AppContext.BaseDirectory);

        return current;
    }
}
