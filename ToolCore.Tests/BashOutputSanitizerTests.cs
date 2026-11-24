namespace ToolCore.Tests;

/// <summary>
/// The sanitizer removes the temp-script path that <c>sh</c> prefixes to every diagnostic.
/// The tests pin BOTH halves of the rule: our own file disappears, the user's file does not.
/// </summary>
public class BashOutputSanitizerTests
{
    private const string TempPath = @"C:\Users\pc\AppData\Local\Temp\invait_8f45813bcf3141d0841baff67db24fae.sh";

    [Fact]
    public void Sanitize_RemovesExactTempPath_KeepsMessage()
    {
        var output = $"{TempPath}: line 1: strings: command not found";

        var result = BashOutputSanitizer.Sanitize(output, TempPath);

        Assert.Equal("line 1: strings: command not found", result);
    }

    [Fact]
    public void Sanitize_RemovesTempPath_EvenWhenTheCallerPassedNoPath()
    {
        // The name pattern alone must suffice: the path is known only inside ExecuteBashAsync,
        // and a caller that forgets to pass it must not resurrect the noise.
        var output = $"{TempPath}: line 1: strings: command not found";

        var result = BashOutputSanitizer.Sanitize(output, tempScriptPath: null);

        Assert.Equal("line 1: strings: command not found", result);
    }

    [Fact]
    public void Sanitize_RemovesTempPath_DashDialect()
    {
        // Ubuntu's /bin/sh is dash, and its diagnostics look different: no "line", no "command",
        // and the marker is a bare relative number after the file name. Both dialects must end up
        // as the useful part only — this is what makes the ProcessExecutor integration test
        // platform-independent instead of asserting Git-sh wording.
        var tempPath = "/tmp/invait_8f45813bcf3141d0841baff67db24fae.sh";
        var output = $"{tempPath}: 1: strings: not found";

        var result = BashOutputSanitizer.Sanitize(output, tempPath);

        Assert.Equal("1: strings: not found", result);

        // The path is not always known (a caller that forgets it), and on Linux that is the branch
        // that runs in CI: the NAME pattern alone must handle this shape too.
        Assert.Equal("1: strings: not found", BashOutputSanitizer.Sanitize(output, tempScriptPath: null));
    }

    [Fact]
    public void Sanitize_RemovesForwardSlashVariant()
    {
        // Git Bash reports the path with forward slashes; the exact-path pass must match it too.
        var output = "C:/Users/pc/AppData/Local/Temp/invait_8f45813bcf3141d0841baff67db24fae.sh: line 2: cd: /nope: No such file or directory";

        var result = BashOutputSanitizer.Sanitize(output, TempPath);

        Assert.Equal("line 2: cd: /nope: No such file or directory", result);
    }

    [Fact]
    public void Sanitize_RemovesTempPathWithSpacesInDirectory()
    {
        // A temp dir like "C:\Users\John Doe\..." is not covered by the name pattern, which is
        // exactly why the exact path is passed in as well.
        var tempPath = @"C:\Users\John Doe\AppData\Local\Temp\invait_8f45813bcf3141d0841baff67db24fae.sh";
        var output = $"{tempPath}: line 1: syntax error near unexpected token";

        var result = BashOutputSanitizer.Sanitize(output, tempPath);

        Assert.Equal("line 1: syntax error near unexpected token", result);
    }

    [Fact]
    public void Sanitize_KeepsTheUsersOwnScriptName()
    {
        // The whole point of stripping by identity: a real script's name is the useful part of
        // the message, so a sanitizer that removed "any .sh" would destroy it.
        var output = "build.sh: line 3: dotnet: command not found";

        var result = BashOutputSanitizer.Sanitize(output, TempPath);

        Assert.Equal("build.sh: line 3: dotnet: command not found", result);
    }

    [Fact]
    public void Sanitize_KeepsAbsolutePathOfTheUsersScript()
    {
        var output = "/home/pc/project/scripts/deploy.sh: line 7: kubectl: command not found";

        var result = BashOutputSanitizer.Sanitize(output, TempPath);

        Assert.Equal("/home/pc/project/scripts/deploy.sh: line 7: kubectl: command not found", result);
    }

    [Fact]
    public void Sanitize_RemovesEveryOccurrence()
    {
        var output = string.Join("\n",
            $"{TempPath}: line 1: strings: command not found",
            "some output",
            $"{TempPath}: line 2: wc: command not found");

        var result = BashOutputSanitizer.Sanitize(output, TempPath);

        Assert.Equal("line 1: strings: command not found\nsome output\nline 2: wc: command not found", result);
    }

    [Fact]
    public void Sanitize_DropsLineThatBecameEmpty()
    {
        // `echo $0` prints nothing but the path; leaving an empty line would be our own noise.
        var output = $"{TempPath}\nreal output";

        var result = BashOutputSanitizer.Sanitize(output, TempPath);

        Assert.Equal("real output", result);
    }

    [Fact]
    public void Sanitize_KeepsLinesThatWereEmptyAlready()
    {
        var output = $"first\n\n{TempPath}: line 1: boom\nlast";

        var result = BashOutputSanitizer.Sanitize(output, TempPath);

        Assert.Equal("first\n\nline 1: boom\nlast", result);
    }

    [Fact]
    public void Sanitize_DoesNotGlueNeighbouringLines()
    {
        // `\s?` in the pattern would eat the newline and merge the two lines into one.
        var output = $"{TempPath}: line 1: boom\nnext line";

        var result = BashOutputSanitizer.Sanitize(output, TempPath);

        Assert.Equal("line 1: boom\nnext line", result);
    }

    [Fact]
    public void Sanitize_RemovesQuotedPath()
    {
        var output = $"'{TempPath}': line 1: boom";

        var result = BashOutputSanitizer.Sanitize(output, TempPath);

        Assert.Equal("line 1: boom", result);
    }

    [Fact]
    public void Sanitize_LeavesUnrelatedOutputUntouched()
    {
        const string output = "Build succeeded.\n0 Warning(s)\n0 Error(s)";

        var result = BashOutputSanitizer.Sanitize(output, TempPath);

        Assert.Equal(output, result);
    }

    [Fact]
    public void Sanitize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, BashOutputSanitizer.Sanitize(null, TempPath));
        Assert.Equal(string.Empty, BashOutputSanitizer.Sanitize(string.Empty, TempPath));
    }

    [Fact]
    public void Sanitize_KeepsFileNameThatMerelyEndsWithOurPrefix()
    {
        // The path prefix must end with a separator and the match must start on a boundary.
        // Without both, the `my` tail landed in the path class and `myinvait_abc.sh` lost its name
        // — the sanitizer destroying exactly the information it exists to protect.
        foreach (var output in new[]
                 {
                     "myinvait_abc.sh: line 1: boom",
                     "my-invait_abc.sh: line 1: boom",
                     "xinvait_abc.sh: boom",
                 })
        {
            Assert.Equal(output, BashOutputSanitizer.Sanitize(output, TempPath));
        }
    }

    [Fact]
    public void Sanitize_RemovesUpperCaseExtension()
    {
        // Windows paths are case-insensitive, and a shell may upper-case the name it echoes.
        var output = @"c:\temp\INVAIT_8F45813BCF3141D0841BAFF67DB24FAE.SH: line 1: boom";

        var result = BashOutputSanitizer.Sanitize(output, TempPath);

        Assert.Equal("line 1: boom", result);
    }

    [Fact]
    public void Sanitize_RemovesBareFileNameWithoutDirectory()
    {
        // `sh` prints the path as it was given; a relative invocation yields no directory part.
        var output = "invait_8f45813bcf3141d0841baff67db24fae.sh: line 1: boom";

        var result = BashOutputSanitizer.Sanitize(output, TempPath);

        Assert.Equal("line 1: boom", result);
    }

    [Fact]
    public void Sanitize_RemovesNodeHostFileNameShape()
    {
        // The Node host names the file invait_<ms>_<rand>.sh; the rule must cover both shapes,
        // otherwise the same bug survives in the other channel.
        var output = "/tmp/invait_1791554079607_abc123.sh: line 1: strings: command not found";

        var result = BashOutputSanitizer.Sanitize(output, tempScriptPath: null);

        Assert.Equal("line 1: strings: command not found", result);
    }
}
