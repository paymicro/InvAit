namespace ToolCore.Tests;

/// <summary>
/// The fixture-path translation that lets mock-filesystem tests run on the ubuntu CI runner.
/// <see cref="MockPaths"/> explains why it is needed; these tests pin the mapping itself, because
/// the Unix half would otherwise be verified only by CI.
/// </summary>
public class MockPathsTests
{
    [Theory]
    [InlineData(@"C:\ws", "/ws")]
    [InlineData(@"C:\ws\sub\file.cs", "/ws/sub/file.cs")]
    [InlineData(@"C:\.editorconfig", "/.editorconfig")]
    [InlineData(@"C:\ws2\file.cs", "/ws2/file.cs")]
    [InlineData(@"C:\data\file.txt", "/data/file.txt")]
    public void For_OnUnix_StripsTheDriveAndUsesForwardSlashes(string windowsPath, string expected)
    {
        Assert.Equal(expected, MockPaths.For(windowsPath, isWindows: false));
    }

    [Theory]
    [InlineData(@"/ws/src/file.txt")]
    [InlineData(@"/")]
    public void For_OnUnix_IsIdempotentForAlreadyAbsolutePaths(string posixPath)
    {
        // `Tree` normalises its keys AND some callers hand it an already-normalised constant, so the
        // translation must survive a second pass — otherwise every such path becomes `//ws/...`.
        Assert.Equal(posixPath, MockPaths.For(posixPath, isWindows: false));
    }

    [Fact]
    public void For_OnUnix_PrefixesARelativePathWithSlash()
    {
        // Relative names appear in fixtures too; the mock needs something absolute.
        Assert.Equal("/sub/file.cs", MockPaths.For(@"sub\file.cs", isWindows: false));
    }

    [Theory]
    [InlineData(@"C:\ws\sub\file.cs")]
    [InlineData(@"/ws/sub/file.cs")]
    public void For_OnWindows_ReturnsTheInputUntouched(string path)
    {
        // On Windows the literals ARE absolute; any rewriting here would be a bug invented by a fix
        // for another platform.
        Assert.Equal(path, MockPaths.For(path, isWindows: true));
    }

    [Fact]
    public void For_OnUnix_KeepsAColonThatIsNotADriveLetter()
    {
        // Only a leading drive letter is a drive. A colon inside a name is legal on Linux and must
        // not be treated as a prefix to strip.
        Assert.Equal("/ws/a:b.cs", MockPaths.For(@"C:\ws\a:b.cs", isWindows: false));
    }
}
