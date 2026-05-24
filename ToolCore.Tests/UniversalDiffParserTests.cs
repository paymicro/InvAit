namespace ToolCore.Tests;

public class UniversalDiffParserTests
{
    #region Basic Match Tests

    [Theory]
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "line2" }, 2, 1)]
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "line1" }, 1, 0)]
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "line3" }, 3, 2)]
    public void FindInFile_ExactMatch_ReturnsCorrectIndex(string[] target, string[] search, int hint, int expected)
    {
        var result = UniversalDiffParser.FindInFile([.. target], [.. search], hint, 0);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "line2" }, 1, 2, 1)]
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "line3" }, 1, 3, 2)]
    public void FindInFile_MatchWithTolerance_ReturnsCorrectIndex(string[] target, string[] search, int hint, int tolerance, int expected)
    {
        var result = UniversalDiffParser.FindInFile([.. target], [.. search], hint, tolerance);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(new[] { "line1", "line2", "line3", "line4" }, new[] { "line2", "line3" }, 2, 1)]
    [InlineData(new[] { "line1", "line2", "line3", "line4" }, new[] { "line1", "line2" }, 1, 0)]
    [InlineData(new[] { "line1", "line2", "line3", "line4" }, new[] { "line3", "line4" }, 3, 2)]
    public void FindInFile_MultilineMatch_ReturnsCorrectIndex(string[] target, string[] search, int hint, int expected)
    {
        var result = UniversalDiffParser.FindInFile([.. target], [.. search], hint, 0);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindInFile_NoMatch_ReturnsMinusOne()
    {
        var result = UniversalDiffParser.FindInFile(["line1", "line2", "line3"], ["notfound"], -1, 0);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_FullSearch_ReturnsFirstMatch()
    {
        var result = UniversalDiffParser.FindInFile(["same", "same", "different"], ["same"], -1, 0);
        Assert.Equal(0, result);
    }

    #endregion

    #region Case Insensitivity Tests

    [Theory]
    [InlineData(new[] { "LINE1", "line2", "LINE3" }, new[] { "line2" }, 2, 1)]
    [InlineData(new[] { "Hello World" }, new[] { "HELLO WORLD" }, -1, 0)]
    [InlineData(new[] { "HeLLo WoRLd" }, new[] { "hElLo wOrLd" }, -1, 0)]
    public void FindInFile_CaseInsensitive_ReturnsCorrectIndex(string[] target, string[] search, int hint, int expected)
    {
        var result = UniversalDiffParser.FindInFile([.. target], [.. search], hint, 5);
        Assert.Equal(expected, result);
    }

    #endregion

    #region Whitespace Tests

    [Theory]
    [InlineData(new[] { "line1  " }, new[] { "line1" }, 1, 0)]
    [InlineData(new[] { "  line2" }, new[] { "line2" }, 1, 0)]
    [InlineData(new[] { "  line1  " }, new[] { "line1" }, 1, 0)]
    [InlineData(new[] { "   bla bla" }, new[] { "bla bla" }, -1, 0)]
    [InlineData(new[] { "bla bla   " }, new[] { "bla bla" }, -1, 0)]
    [InlineData(new[] { "  bla bla  " }, new[] { "   bla bla   " }, -1, 0)]
    public void FindInFile_TrimsWhitespace_ReturnsCorrectIndex(string[] target, string[] search, int hint, int expected)
    {
        var result = UniversalDiffParser.FindInFile([.. target], [.. search], hint, 5);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindInFile_LeadingTrailingWhitespace_HandledCorrectly()
    {
        var target = new List<string> { "  line1  ", "  line2  ", "  line3  " };
        var search = new List<string> { "  line2  " };

        var result = UniversalDiffParser.FindInFile(target, search, -1, 0);

        Assert.Equal(1, result);
    }

    #endregion

    #region Hint Tests

    [Theory]
    [InlineData(new[] { "line1", "line2", "line3", "line4", "line5" }, new[] { "line2", "line3" }, 2, 1)]
    [InlineData(new[] { "line1", "line2", "line3", "line4", "line5" }, new[] { "line3", "line4" }, 2, 2)]
    [InlineData(new[] { "line1", "line2", "line3", "line4", "line5" }, new[] { "line4", "line5" }, 2, 3)]
    [InlineData(new[] { "line1", "line2", "line3", "line4", "line5" }, new[] { "line1", "line2" }, 3, 0)]
    public void FindInFile_WithHint_ReturnsCorrectIndex(string[] target, string[] search, int hint, int expected)
    {
        var result = UniversalDiffParser.FindInFile([.. target], [.. search], hint, 5);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindInFile_FallbackToFullSearch_WhenHintNotFoundWithinTolerance()
    {
        var target = new List<string> { "line1", "line2", "line3", "line4", "line5" };
        var search = new List<string> { "line4", "line5" };

        var result = UniversalDiffParser.FindInFile(target, search, 1, 1);

        Assert.Equal(3, result);
    }

    [Theory]
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "line2" }, 2, -5, 1)]
    [InlineData(new[] { "line1", "line2" }, new[] { "line2" }, 2, -10, 1)]
    public void FindInFile_NegativeTolerance_DefaultsToZero(string[] target, string[] search, int hint, int tolerance, int expected)
    {
        var result = UniversalDiffParser.FindInFile([.. target], [.. search], hint, tolerance);
        Assert.Equal(expected, result);
    }

    #endregion

    #region Empty Lines Tests

    [Theory]
    [InlineData(new[] { "line1", "", "line2" }, new[] { "line2" }, 3, 2)]
    [InlineData(new[] { "line1", "", "", "", "line2" }, new[] { "line2" }, 5, 4)]
    [InlineData(new[] { "", "", "line1", "line2" }, new[] { "line1" }, 3, 2)]
    [InlineData(new[] { "line1", "line2", "", "" }, new[] { "line2" }, 2, 1)]
    public void FindInFile_WithEmptyLines_ReturnsCorrectIndex(string[] target, string[] search, int hint, int expected)
    {
        var result = UniversalDiffParser.FindInFile([.. target], [.. search], hint, 5);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(new[] { "line1", "", "line2", "line3" }, new[] { "line1", "", "line2" }, -1, 0)]
    [InlineData(new[] { "start", "middle", "", "end" }, new[] { "middle", "", "end" }, -1, 1)]
    [InlineData(new[] { "line1", "", "", "line2" }, new[] { "", "" }, -1, 1)]
    public void FindInFile_EmptyLinesInSearch_MatchesCorrectly(string[] target, string[] search, int hint, int expected)
    {
        var result = UniversalDiffParser.FindInFile([.. target], [.. search], hint, 5);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(new[] { "line1", "content here", "line3" }, new[] { "" }, -1, -1)]
    [InlineData(new[] { "start", "middle", "content", "end" }, new[] { "middle", "", "end" }, -1, -1)]
    public void FindInFile_EmptySearch_DoesNotMatchContent(string[] target, string[] search, int hint, int expected)
    {
        var result = UniversalDiffParser.FindInFile([.. target], [.. search], hint, 0);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindInFile_EmptySearchLine_MatchesEmptyTargetLine()
    {
        var result = UniversalDiffParser.FindInFile(["line1", "", "line3"], [""], -1, 0);
        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_WhitespaceOnlySearchLine_MatchesWhitespaceTarget()
    {
        var result = UniversalDiffParser.FindInFile(["line1", "   ", "line3"], ["   "], -1, 0);
        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_WhitespaceOnlySearchLine_DoesNotMatchContent()
    {
        var result = UniversalDiffParser.FindInFile(["line1", "some content", "line3"], ["   "], -1, 0);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_WhitespaceOnlyLines_HandledAsEmpty()
    {
        var target = new List<string> { "line1", "   ", "line2" };
        var search = new List<string> { "line1", "", "line2" };

        var result = UniversalDiffParser.FindInFile(target, search, -1, 5);

        Assert.Equal(0, result);
    }

    #endregion

    #region Multiple Similar Strings Tests

    [Fact]
    public void FindInFile_MultipleSimilarBlocks_FindsFirst()
    {
        var target = new List<string>
        {
            "public void Method()", "{", "    var x = 1;", "}",
            "public void Method()", "{", "    var x = 2;", "}"
        };
        var search = new List<string> { "public void Method()", "{", "    var x = 1;", "}" };

        var result = UniversalDiffParser.FindInFile(target, search, -1, 5);

        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_MultipleSimilarBlocks_FindsSecond()
    {
        var target = new List<string>
        {
            "public void Method()", "{", "    var x = 1;", "}",
            "public void Method()", "{", "    var x = 2;", "}"
        };
        var search = new List<string> { "public void Method()", "{", "    var x = 2;", "}" };

        var result = UniversalDiffParser.FindInFile(target, search, -1, 5);

        Assert.Equal(4, result);
    }

    [Fact]
    public void FindInFile_MultipleSimilarSingleLines_FindsCorrect()
    {
        var target = new List<string>
        {
            "Console.WriteLine(\"Hello\");",
            "Console.WriteLine(\"Hello!\");",
            "Console.WriteLine(\"Hello?\");"
        };

        var result = UniversalDiffParser.FindInFile(target, ["Console.WriteLine(\"Hello!\");"], -1, 5);

        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_MultipleIdenticalBlocks_FindsFirst()
    {
        var target = new List<string> { "line1", "line2", "line1", "line2" };
        var search = new List<string> { "line1", "line2" };

        var result = UniversalDiffParser.FindInFile(target, search, -1, 5);

        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_OverlappingPatterns_FindsCorrect()
    {
        var target = new List<string> { "A", "A", "B" };
        var search = new List<string> { "A", "B" };

        var result = UniversalDiffParser.FindInFile(target, search, -1, 5);

        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_DistinguishesEmptyLinesFromContent()
    {
        var target = new List<string>
        {
            "public void Method()", "{", "", "    var x = 1;", "}",
            "public void Method()", "{", "    var x = 2;", "}"
        };
        var search = new List<string> { "public void Method()", "{", "    var x = 2;", "}" };

        var result = UniversalDiffParser.FindInFile(target, search, -1, 5);

        Assert.Equal(5, result);
    }

    #endregion

    #region Real World Scenarios

    [Fact]
    public void FindInFile_RealWorldDiffScenario_Works()
    {
        var fileContent = new List<string>
        {
            "namespace MyApp", "{", "    public class Test", "{",
            "        public void Method()", "        {", "            // Old code", "        }", "    }", "}"
        };

        var search = new List<string> { "        {", "            // Old code", "        }" };

        var result = UniversalDiffParser.FindInFile(fileContent, search, 7, 2);

        Assert.Equal(5, result);
    }

    [Fact]
    public void FindInFile_MultilineWithEmptyLine_DoesNotMatchIfTargetHasContent()
    {
        var target = new List<string> { "start", "middle", "content", "end" };
        var search = new List<string> { "middle", "", "end" };

        var result = UniversalDiffParser.FindInFile(target, search, -1, 0);

        Assert.Equal(-1, result);
    }

    #endregion
}
