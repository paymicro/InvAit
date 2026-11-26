namespace ToolCore.Tests;

/// <summary>
/// Paths for the MOCK filesystem, written the way the real tree looks on Windows and translated to
/// the running platform at the boundary.
/// <para>
/// WHY THIS EXISTS: `MockFileSystem` (System.IO.Abstractions.TestingHelpers 23.0.0) offers no
/// platform override — it inherits the RUNNING platform's path rules. On Linux a Windows literal is
/// therefore not an absolute path at all: `Path.GetFullPath(@"C:\ws\sub\file.cs")` prefixes the
/// working directory, every `.editorconfig` lookup lands on `/C:\ws/...` where nothing exists, and
/// `AllFiles` reports `/C:\data\file.txt`. `\` is also a perfectly legal FILE-NAME character there,
/// so `C:\ws` becomes one strange file instead of a directory.
/// </para>
/// <para>
/// The fixtures keep the Windows spelling on purpose — it is what the product's own paths look like
/// (`InvAit` is net48 + VS SDK, Windows only) — and pay the tax here instead: everything handed to
/// the mock goes through <see cref="Mock"/>, which yields a path that IS absolute on this machine.
/// This is a fixture shim, not a behaviour change: `EditorConfigResolver` and `FileWriter` still
/// receive one absolute path, exactly as the VS host passes one.
/// </para>
/// <para>
/// A literal that reaches the mock UNTRANSLATED passes on Windows and fails on the ubuntu CI runner.
/// </para>
/// </summary>
internal static class MockPaths
{
    /// <summary>
    /// Translates a Windows-style path for the mock on the current platform: `C:\ws\x` becomes
    /// `/ws/x` on Unix. Idempotent, so a path may pass through more than once.
    /// </summary>
    public static string Mock(string windowsPath) => For(windowsPath, OperatingSystem.IsWindows());

    /// <summary>
    /// The translation itself, with the platform as a PARAMETER — the Unix half would otherwise be
    /// exercised only by CI, i.e. by a run nobody can repeat locally before pushing.
    /// </summary>
    internal static string For(string windowsPath, bool isWindows)
    {
        if (isWindows)
            return windowsPath;

        var path = StripDrive(windowsPath.Replace('\\', '/'));
        return path.StartsWith('/') ? path : "/" + path;
    }

    /// <summary>`C:/ws` -> `/ws`. Only a leading drive letter is removed; a colon elsewhere stays.</summary>
    private static string StripDrive(string path)
        => path.Length >= 2 && path[1] == ':' ? path[2..] : path;
}
