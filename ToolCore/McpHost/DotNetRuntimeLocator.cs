using System.IO;

namespace ToolCore.McpHost;

public static class DotNetRuntimeLocator
{
    public const string RequiredMajorVersion = "10";

    public const string InstallUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";

    public const string InstallHint =
        "MCP host requires the .NET " + RequiredMajorVersion + " runtime, which was not found on this machine. " +
        "Install it from " + InstallUrl + " (any SDK or '.NET Runtime' package) and restart Visual Studio. " +
        "Visual Studio 2026 ships it by default; on Visual Studio 2022 it may need to be installed manually.";

    public static bool IsRuntimeInstalled()
    {
        return TryFindRuntimeDirectory(RequiredMajorVersion, out _);
    }

    public static bool TryGetDotNetExecutable(out string dotNetExe)
    {
        dotNetExe = string.Empty;
        if (!TryGetDotNetRoot(out var root))
            return false;

        var exeName = Path.DirectorySeparatorChar == '\\' ? "dotnet.exe" : "dotnet";
        var candidate = Path.Combine(root, exeName);
        if (File.Exists(candidate))
        {
            dotNetExe = candidate;
            return true;
        }

        return false;
    }

    internal static bool TryFindRuntimeDirectory(string majorVersionPrefix, out string runtimeDirectory)
    {
        runtimeDirectory = string.Empty;
        foreach (var root in GetDotNetRootCandidates())
        {
            var sharedDir = Path.Combine(root, "shared", "Microsoft.NETCore.App");
            if (!Directory.Exists(sharedDir))
                continue;

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(sharedDir))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith(majorVersionPrefix + ".", StringComparison.Ordinal))
                    {
                        runtimeDirectory = dir;
                        return true;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    public static bool TryGetDotNetRoot(out string dotNetRoot)
    {
        foreach (var candidate in GetDotNetRootCandidates())
        {
            dotNetRoot = candidate;
            return true;
        }

        dotNetRoot = string.Empty;
        return false;
    }

    private static IEnumerable<string> GetDotNetRootCandidates()
    {
        var candidates = new List<string>();

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                candidates.Add(path.TrimEnd(Path.DirectorySeparatorChar));
        }

        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        Add(Environment.GetEnvironmentVariable("ProgramFiles") is var pf ? Path.Combine(pf ?? string.Empty, "dotnet") : null);
        Add(Environment.GetEnvironmentVariable("ProgramFiles(x86)") is var pf86 ? Path.Combine(pf86 ?? string.Empty, "dotnet") : null);
        Add(Environment.GetEnvironmentVariable("LOCALAPPDATA") is var lad ? Path.Combine(lad ?? string.Empty, "Microsoft", "dotnet") : null);

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists);
    }
}
