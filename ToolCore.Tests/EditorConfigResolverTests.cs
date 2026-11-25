using System.IO.Abstractions.TestingHelpers;
using System.Text;

namespace ToolCore.Tests;

/// <summary>
/// `.editorconfig` resolution: only `charset` and `end_of_line` matter, and a tree that declares
/// them must be honoured instead of the hard-coded UTF-8-with-BOM that `create_file` used to write.
/// </summary>
public class EditorConfigResolverTests
{
    private const string _root = @"C:\ws";

    private static EditorConfigResolver Resolver(MockFileSystem fs) => new(fs);

    private static MockFileSystem Tree(params (string Path, string Text)[] files)
    {
        var data = new Dictionary<string, MockFileData>();
        foreach (var (path, text) in files)
            data[path] = new MockFileData(text);
        data[_root + @"\.gitkeep"] = new MockFileData(string.Empty);
        return new MockFileSystem(data);
    }

    [Fact]
    public void Resolve_NoEditorConfig_ReturnsEmpty()
    {
        var fs = Tree();

        var settings = Resolver(fs).Resolve(_root + @"\sub\file.cs");

        Assert.True(settings.IsEmpty);
        Assert.Null(settings.Charset);
        Assert.Null(settings.EndOfLine);
        Assert.Null(settings.Separator);
        Assert.Null(settings.CreateEncoding());
    }

    [Fact]
    public void Resolve_InheritsFromParentDirectory()
    {
        var fs = Tree((_root + @"\.editorconfig", "root = true\n[*]\ncharset = utf-8-bom\nend_of_line = crlf\n"));

        var settings = Resolver(fs).Resolve(_root + @"\src\deep\file.cs");

        Assert.Equal("utf-8-bom", settings.Charset);
        Assert.Equal("crlf", settings.EndOfLine);
        Assert.Equal("\r\n", settings.Separator);
        Assert.Equal(new UTF8Encoding(true).GetPreamble(), settings.CreateEncoding()!.GetPreamble());
    }

    [Fact]
    public void Resolve_ClosestFileWins()
    {
        var fs = Tree(
            (_root + @"\.editorconfig", "root = true\n[*]\ncharset = utf-8\nend_of_line = lf\n"),
            (_root + @"\src\.editorconfig", "[*]\ncharset = utf-8-bom\n"));

        var settings = Resolver(fs).Resolve(_root + @"\src\file.cs");

        // charset is overridden by the closer file, end_of_line is inherited from the root one.
        Assert.Equal("utf-8-bom", settings.Charset);
        Assert.Equal("lf", settings.EndOfLine);
    }

    [Fact]
    public void Resolve_RootTrueStopsTheWalk()
    {
        // The PARENT supplies a property the closer file does NOT set. The old version of this test
        // had the closer file override the same property, so it stayed green even when
        // `root = true` was ignored outright — a tautology, not a test.
        var fs = Tree(
            (@"C:\.editorconfig", "[*]\nend_of_line = crlf\n"),
            (_root + @"\.editorconfig", "root = true\n[*]\ncharset = utf-8\n"));

        var settings = Resolver(fs).Resolve(_root + @"\file.cs");

        Assert.Equal("utf-8", settings.Charset);
        Assert.Null(settings.EndOfLine);
    }

    [Fact]
    public void Resolve_Unset_RemovesAnInheritedValue()
    {
        // `unset` means "reset to the tool default", not "the charset is literally unset": the
        // closer file clears both properties the parent declared.
        var fs = Tree(
            (_root + @"\.editorconfig", "root = true\n[*]\ncharset = latin1\nend_of_line = crlf\n"),
            (_root + @"\src\.editorconfig", "[*]\ncharset = unset\nend_of_line = unset\n"));

        var settings = Resolver(fs).Resolve(_root + @"\src\file.cs");

        Assert.True(settings.IsEmpty);
        Assert.Null(settings.CreateEncoding());
        Assert.Null(settings.Separator);
    }

    [Fact]
    public void Resolve_Unset_InALaterSection_RemovesAnEarlierValueOfTheSameFile()
    {
        var fs = Tree((_root + @"\.editorconfig", "root = true\n[*]\ncharset = latin1\n[*.txt]\ncharset = unset\n"));

        var txt = Resolver(fs).Resolve(_root + @"\file.txt");
        var cs = Resolver(fs).Resolve(_root + @"\file.cs");

        Assert.Null(txt.Charset);
        Assert.Equal("latin1", cs.Charset);
    }

    [Fact]
    public void Resolve_InvalidSectionPattern_MatchesNothingInsteadOfThrowing()
    {
        // One broken header used to throw ArgumentException out of Resolve and break EVERY write in
        // the tree. It has to match nothing — and the valid sections must keep working.
        var fs = Tree((_root + @"\.editorconfig",
            "root = true\n[*.[!]x]\ncharset = latin1\n[*.[z-a]x]\ncharset = utf-16le\n[*.cs]\ncharset = utf-8-bom\n"));

        var settings = Resolver(fs).Resolve(_root + @"\file.cs");

        Assert.Equal("utf-8-bom", settings.Charset);
    }

    [Fact]
    public void Resolve_ReversedNumericRange_MatchesNothing()
    {
        // `{9..1}` addresses NOTHING. It used to fall into the "too wide to expand" branch and
        // became `[0-9]+`, so `[src{9..1}/*.cs]` applied its charset to every numbered file in the
        // tree — the exact opposite of the promise that a header which does not compile matches
        // nothing. Two unrelated conditions must not share one fallback.
        var fs = Tree((_root + @"\.editorconfig", "root = true\n[f{9..1}.cs]\ncharset = latin1\n"));

        Assert.Null(Resolver(fs).Resolve(_root + @"\f5.cs").Charset);
        Assert.Null(Resolver(fs).Resolve(_root + @"\f9.cs").Charset);
        Assert.Null(Resolver(fs).Resolve(_root + @"\f1.cs").Charset);
        // The LITERAL name too: deleting the reversed-range branch leaves an EMPTY alternation,
        // `f(?:\.cs)`, which still matches none of the three above but DOES match `f.cs`. Without
        // this line the guard could be removed with every assertion still green.
        Assert.Null(Resolver(fs).Resolve(_root + @"\f.cs").Charset);
    }

    [Fact]
    public void Resolve_WideNumericRange_KeepsTheDocumentedOverApproximation()
    {
        // A range too large to expand stays `[0-9]+` ON PURPOSE — a small regex instead of 200
        // alternatives. That over-approximation is documented and must survive the fix above.
        var fs = Tree((_root + @"\.editorconfig", "root = true\n[f{1..200}.cs]\ncharset = latin1\n"));

        Assert.Equal("latin1", Resolver(fs).Resolve(_root + @"\f5.cs").Charset);
    }

    [Fact]
    public void Resolve_SiblingDirectoryWithTheSameNamePrefix_IsNotInside()
    {
        // `C:\ws2` starts with `C:\ws` as a STRING but is not in that tree.
        var fs = Tree((_root + @"\.editorconfig", "root = true\n[*]\ncharset = latin1\n"));

        var settings = Resolver(fs).Resolve(@"C:\ws2\file.cs");

        Assert.Null(settings.Charset);
    }

    [Theory]
    [InlineData("file.cs", "[*.cs]\ncharset = utf-8-bom\n", "utf-8-bom")]
    [InlineData("file.ts", "[*.cs]\ncharset = utf-8-bom\n", null)]
    [InlineData("file.razor", "[*.{cs,razor}]\ncharset = latin1\n", "latin1")]
    [InlineData("file.cs", "[*]\ncharset = utf-16le\n", "utf-16le")]
    [InlineData("file.cs", "[*.csproj]\ncharset = utf-8-bom\n", null)]
    public void Resolve_SectionPatternMatching(string fileName, string config, string? expectedCharset)
    {
        var fs = Tree((_root + @"\.editorconfig", "root = true\n" + config));

        var settings = Resolver(fs).Resolve(_root + @"\" + fileName);

        Assert.Equal(expectedCharset, settings.Charset);
    }

    [Fact]
    public void Resolve_PatternWithSlash_IsAnchoredToItsDirectory()
    {
        var fs = Tree((_root + @"\.editorconfig", "root = true\n[src/**/*.cs]\ncharset = utf-8-bom\n"));

        var inside = Resolver(fs).Resolve(_root + @"\src\a\b\file.cs");
        var outside = Resolver(fs).Resolve(_root + @"\other\file.cs");

        Assert.Equal("utf-8-bom", inside.Charset);
        Assert.Null(outside.Charset);
    }

    [Fact]
    public void Resolve_LaterSectionWins()
    {
        var fs = Tree((_root + @"\.editorconfig", "root = true\n[*]\ncharset = utf-8\n[*.cs]\ncharset = utf-8-bom\n"));

        var settings = Resolver(fs).Resolve(_root + @"\file.cs");

        Assert.Equal("utf-8-bom", settings.Charset);
    }

    [Fact]
    public void Resolve_UnknownCharset_IsKeptButDoesNotProduceAnEncoding()
    {
        var fs = Tree((_root + @"\.editorconfig", "root = true\n[*]\ncharset = utf-9\n"));

        var settings = Resolver(fs).Resolve(_root + @"\file.cs");

        Assert.Equal("utf-9", settings.Charset);
        // No guessing: the caller falls back to its own default and says so.
        Assert.Null(settings.CreateEncoding());
    }

    [Fact]
    public void Resolve_CommentsAndBlankLinesAreIgnored()
    {
        var fs = Tree((_root + @"\.editorconfig",
            "# comment\r\n\r\n; another\r\nroot = true\r\n[*]\r\ncharset = utf-8\r\n"));

        var settings = Resolver(fs).Resolve(_root + @"\file.cs");

        Assert.Equal("utf-8", settings.Charset);
    }

    [Fact]
    public void Resolve_ValuesAndKeysAreCaseInsensitive()
    {
        var fs = Tree((_root + @"\.editorconfig", "root = true\n[*]\nCHARSET = UTF-8-BOM\nEnd_Of_Line = CRLF\n"));

        var settings = Resolver(fs).Resolve(_root + @"\file.cs");

        Assert.Equal("utf-8-bom", settings.Charset);
        Assert.Equal("crlf", settings.EndOfLine);
    }

    [Fact]
    public void Resolve_UnreadableEditorConfig_ContributesNothingInsteadOfBreakingTheWrite()
    {
        // The promise in the class doc — one rotten `.editorconfig` above the tree must not break
        // every write in it — was implemented for the PATTERN half only. A config that cannot be
        // READ (held by another process, ACLs, a network share that went away) threw out of
        // `Resolve`, and with it every `create_file` / `edit_file` under that directory failed.
        var fs = Tree();
        fs.AddFile(_root + @"\.editorconfig", new MockFileData("root = true\n[*]\ncharset = latin1\n")
        {
            // `FileShare.None` = no reader may open it: the mock's equivalent of a locked file.
            AllowedFileShare = FileShare.None
        });

        var settings = Resolver(fs).Resolve(_root + @"\file.cs");

        // Degrades to "keep the caller's default", like an unsupported pattern does.
        Assert.True(settings.IsEmpty);
    }

    [Fact]
    public void Resolve_EditorConfigWithABom_StillResolvesTheFirstSection()
    {
        // Visual Studio and Notepad save `.editorconfig` WITH a BOM by default on Windows. A parser
        // that sees the BOM as a character drops the FIRST header — and with it every setting in the
        // file, silently, because the write then falls back to its default encoding.
        //
        // The BOM sits on the SECTION header on purpose: this host removes it in the DECODER
        // (`File.ReadAllText` detects it), so a fixture that puts it on `root = true` would assert
        // nothing — the parser never sees U+FEFF either way. On the header the test is only green
        // while the decoder keeps doing that job. The VS Code twin needs no decoder and covers the
        // same file shape through `trim()`.
        var fs = Tree((_root + @"\.editorconfig", "\uFEFF[*]\ncharset = latin1\n"));

        var settings = Resolver(fs).Resolve(_root + @"\file.cs");

        Assert.Equal("latin1", settings.Charset);
    }

    [Fact]
    public void Resolve_UnparseableLineInsideASection_DoesNotEndTheSection()
    {
        // A hand-edited config carries junk: a stray word, a header missing its `]`, a property with
        // no value. Each must be skipped WITHOUT resetting the current section — otherwise every
        // setting below the junk line disappears and the file is written with defaults.
        var fs = Tree((_root + @"\.editorconfig",
            "root = true\n[*]\nthis line is junk\n[*\ncharset =\ncharset = latin1\n"));

        var settings = Resolver(fs).Resolve(_root + @"\file.cs");

        Assert.Equal("latin1", settings.Charset);
    }
}
