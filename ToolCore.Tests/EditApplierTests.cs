namespace ToolCore.Tests;

public class EditApplierTests
{
    #region Basic Match Tests

    [Theory]
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "line2" }, 2, 1)]
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "line1" }, 1, 0)]
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "line3" }, 3, 2)]
    public void FindInFile_ExactMatch_ReturnsCorrectIndex(string[] target, string[] search, int hint, int expected)
    {
        var result = EditApplier.FindInFile([.. target], [.. search], hint, 0);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "line2" }, 1, 2, 1)]
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "line3" }, 1, 3, 2)]
    public void FindInFile_MatchWithTolerance_ReturnsCorrectIndex(string[] target, string[] search, int hint, int tolerance, int expected)
    {
        var result = EditApplier.FindInFile([.. target], [.. search], hint, tolerance);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(new[] { "line1", "line2", "line3", "line4" }, new[] { "line2", "line3" }, 2, 1)]
    [InlineData(new[] { "line1", "line2", "line3", "line4" }, new[] { "line1", "line2" }, 1, 0)]
    [InlineData(new[] { "line1", "line2", "line3", "line4" }, new[] { "line3", "line4" }, 3, 2)]
    public void FindInFile_MultilineMatch_ReturnsCorrectIndex(string[] target, string[] search, int hint, int expected)
    {
        var result = EditApplier.FindInFile([.. target], [.. search], hint, 0);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindInFile_NoMatch_ReturnsMinusOne()
    {
        var result = EditApplier.FindInFile(["line1", "line2", "line3"], ["notfound"], -1, 0);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_FullSearch_ReturnsFirstMatch()
    {
        var result = EditApplier.FindInFile(["same", "same", "different"], ["same"], -1, 0);
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
        var result = EditApplier.FindInFile([.. target], [.. search], hint, 5);
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
        var result = EditApplier.FindInFile([.. target], [.. search], hint, 5);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindInFile_LeadingTrailingWhitespace_HandledCorrectly()
    {
        var target = new List<string> { "  line1  ", "  line2  ", "  line3  " };
        var search = new List<string> { "  line2  " };

        var result = EditApplier.FindInFile(target, search, -1, 0);

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
        var result = EditApplier.FindInFile([.. target], [.. search], hint, 5);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindInFile_FallbackToFullSearch_WhenHintNotFoundWithinTolerance()
    {
        var target = new List<string> { "line1", "line2", "line3", "line4", "line5" };
        var search = new List<string> { "line4", "line5" };

        var result = EditApplier.FindInFile(target, search, 1, 1);

        Assert.Equal(3, result);
    }

    [Theory]
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "line2" }, 2, -5, 1)]
    [InlineData(new[] { "line1", "line2" }, new[] { "line2" }, 2, -10, 1)]
    public void FindInFile_NegativeTolerance_DefaultsToZero(string[] target, string[] search, int hint, int tolerance, int expected)
    {
        var result = EditApplier.FindInFile([.. target], [.. search], hint, tolerance);
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
        var result = EditApplier.FindInFile([.. target], [.. search], hint, 5);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(new[] { "line1", "", "line2", "line3" }, new[] { "line1", "", "line2" }, -1, 0)]
    [InlineData(new[] { "start", "middle", "", "end" }, new[] { "middle", "", "end" }, -1, 1)]
    [InlineData(new[] { "line1", "", "", "line2" }, new[] { "", "" }, -1, 1)]
    public void FindInFile_EmptyLinesInSearch_MatchesCorrectly(string[] target, string[] search, int hint, int expected)
    {
        var result = EditApplier.FindInFile([.. target], [.. search], hint, 5);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(new[] { "line1", "content here", "line3" }, new[] { "" }, -1, -1)]
    [InlineData(new[] { "start", "middle", "content", "end" }, new[] { "middle", "", "end" }, -1, -1)]
    public void FindInFile_EmptySearch_DoesNotMatchContent(string[] target, string[] search, int hint, int expected)
    {
        var result = EditApplier.FindInFile([.. target], [.. search], hint, 0);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindInFile_EmptySearchLine_MatchesEmptyTargetLine()
    {
        var result = EditApplier.FindInFile(["line1", "", "line3"], [""], -1, 0);
        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_WhitespaceOnlySearchLine_MatchesWhitespaceTarget()
    {
        var result = EditApplier.FindInFile(["line1", "   ", "line3"], ["   "], -1, 0);
        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_WhitespaceOnlySearchLine_DoesNotMatchContent()
    {
        var result = EditApplier.FindInFile(["line1", "some content", "line3"], ["   "], -1, 0);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_WhitespaceOnlyLines_HandledAsEmpty()
    {
        var target = new List<string> { "line1", "   ", "line2" };
        var search = new List<string> { "line1", "", "line2" };

        var result = EditApplier.FindInFile(target, search, -1, 5);

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

        var result = EditApplier.FindInFile(target, search, -1, 5);

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

        var result = EditApplier.FindInFile(target, search, -1, 5);

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

        var result = EditApplier.FindInFile(target, ["Console.WriteLine(\"Hello!\");"], -1, 5);

        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_MultipleIdenticalBlocks_FindsFirst()
    {
        var target = new List<string> { "line1", "line2", "line1", "line2" };
        var search = new List<string> { "line1", "line2" };

        var result = EditApplier.FindInFile(target, search, -1, 5);

        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_OverlappingPatterns_FindsCorrect()
    {
        var target = new List<string> { "A", "A", "B" };
        var search = new List<string> { "A", "B" };

        var result = EditApplier.FindInFile(target, search, -1, 5);

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

        var result = EditApplier.FindInFile(target, search, -1, 5);

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

        var result = EditApplier.FindInFile(fileContent, search, 7, 2);

        Assert.Equal(5, result);
    }

    [Fact]
    public void FindInFile_MultilineWithEmptyLine_DoesNotMatchIfTargetHasContent()
    {
        var target = new List<string> { "start", "middle", "content", "end" };
        var search = new List<string> { "middle", "", "end" };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(-1, result);
    }

    #endregion

    #region Single Line Partial (Substring) Match Tests

    [Fact]
    public void FindInFile_SingleLineTruncatedAtBothEnds_Matches()
    {
        // Regression: the model replaced only a fragment of a line — the search line
        // is trimmed at BOTH the start and the end (a substring of the target line).
        var target = new List<string> { "var fileContent = new List<string>" };
        var search = new List<string> { "List<string" };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_SingleLineTruncatedAtBothEnds_WithIndentation_Matches()
    {
        var target = new List<string>
        {
            "public void Method()", "{", "    var fileContent = new List<string>();", "}"
        };
        var search = new List<string> { "fileContent = new List<string" };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(2, result);
    }

    [Fact]
    public void FindInFile_SingleLineSubstringMatch_CaseInsensitive()
    {
        var target = new List<string> { "var fileContent = new List<string>" };
        var search = new List<string> { "list<string" };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_SingleLineSubstringMatch_WithHint_Matches()
    {
        var target = new List<string> { "line1", "var fileContent = new List<string>", "line3" };
        var search = new List<string> { "List<string" };

        var result = EditApplier.FindInFile(target, search, 2, 1);

        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_ShortSubstring_Matches()
    {
        // MinPartialLength is gone: even a short search line matches as a substring.
        var target = new List<string> { "var fileContent = new List<string>" };
        var search = new List<string> { "st" };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_SingleLineSubstringNotPresent_DoesNotMatch()
    {
        var target = new List<string> { "var fileContent = new List<string>" };
        var search = new List<string> { "List<int" };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_SingleLineSubstringMatch_PrefersExactMatchElsewhere()
    {
        // Exact match on a later line wins over a fragment match on an earlier one.
        var target = new List<string> { "x List<string y", "List<string" };
        var search = new List<string> { "List<string" };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(1, result);
    }

    #endregion

    #region Partial Boundary Line Tests

    [Theory]
    // Last line truncated at the end (search is a prefix of the target line)
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "line1", "line2", "lin" }, 0)]
    // First line truncated at the start (search is a suffix of the target line)
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "ne1", "line2", "line3" }, 0)]
    [InlineData(new[] { "line1", "line2", "line3" }, new[] { "ine1", "line2", "line3" }, 0)]
    public void FindInFile_TruncatedBoundaryLines_Matches(string[] target, string[] search, int expected)
    {
        // The first search line is truncated at the start and the last at the end —
        // the block must still be found via partial boundary matching.
        var result = EditApplier.FindInFile([.. target], [.. search], -1, 0);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindInFile_FirstLineTruncatedAtEnd_DoesNotMatch()
    {
        // The first line may only be truncated at the START, not at the end:
        // search line extending the target line must not match.
        var target = new List<string> { "line1", "line2", "line3" };
        var search = new List<string> { "line1 extra", "line2", "line3" };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_LastLineTruncatedAtStart_DoesNotMatch()
    {
        // The last line may only be truncated at the END, not at the start.
        var target = new List<string> { "line1", "line2", "line3" };
        var search = new List<string> { "line1", "line2", "xline3" };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_TruncatedLastLine_RealWorldScenario()
    {
        // Regression: the model cut the last line of oldStr mid-token —
        // the search must still find the block.
        var target = new List<string>
        {
            "public void Method()", "{", "    var x = 1;", "    DoSomething(value);", "}"
        };
        var search = new List<string>
        {
            "    var x = 1;", "    DoSomething(val"
        };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(2, result);
    }

    [Fact]
    public void FindInFile_TruncatedFirstLine_RealWorldScenario()
    {
        // The model lost the leading characters of the first line (indentation + tokens) —
        // the search line is a suffix of the target line and must still match.
        var target = new List<string>
        {
            "public void Method()", "{", "    var someLongVariable = ComputeValue();", "}"
        };
        var search = new List<string>
        {
            "ar someLongVariable = ComputeValue();", "}"
        };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(2, result);
    }

    [Fact]
    public void FindInFile_ShortBoundaryLines_StillRequireExactMatch()
    {
        // Short lines like "}" must NOT match partially — otherwise "}" would
        // match any line starting with "}" and produce false positives.
        var target = new List<string> { "a", "b", "}" };
        var search = new List<string> { "b", "} extra" };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_MiddleLineTruncated_DoesNotMatch()
    {
        // Only boundary lines match partially; a truncated middle line must fail.
        var target = new List<string> { "alpha", "beta", "gamma", "delta" };
        var search = new List<string> { "alpha", "gam", "delta" };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_PartialBoundaryMatch_StillRequiresFullMiddleMatch()
    {
        // First line truncated at start ("alp" is a suffix of "alpha"),
        // last line truncated at end ("delt" is a prefix of "delta").
        var target = new List<string> { "alpha", "beta", "gamma", "delta" };
        var search = new List<string> { "lpha", "beta", "gamma", "delt" };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_PartialBoundaryMatch_CaseInsensitive()
    {
        var target = new List<string> { "public void Method()", "{", "    var x = 1;", "}" };
        var search = new List<string> { "    VAR X = 1;", "}" };

        var result = EditApplier.FindInFile(target, search, -1, 0);

        Assert.Equal(2, result);
    }

    #endregion

    #region ApplyEdit Tests

    [Fact]
    public void ApplyEdit_ExactMatch_ReplacesWholeLine()
    {
        var target = new List<string> { "line1", "line2", "line3" };

        var result = EditApplier.ApplyEdit(target, ["line2"], ["replaced"], -1, 0);

        Assert.Equal(1, result);
        Assert.Equal(["line1", "replaced", "line3"], target);
    }

    [Fact]
    public void ApplyEdit_MultilineExactMatch_ReplacesBlock()
    {
        var target = new List<string> { "a", "b", "c", "d" };

        var result = EditApplier.ApplyEdit(target, ["b", "c"], ["B", "C", "C2"], -1, 0);

        Assert.Equal(1, result);
        Assert.Equal(["a", "B", "C", "C2", "d"], target);
    }

    [Fact]
    public void ApplyEdit_SingleLineFragmentMatch_ReplacesOnlyFragment()
    {
        // Regression: the model replaced only a fragment of a line — the search line
        // is trimmed at BOTH the start and the end. Only the matched fragment must be
        // replaced, the rest of the original line is preserved.
        var target = new List<string> { "var fileContent = new List<string>" };

        var result = EditApplier.ApplyEdit(target, ["List<string"], ["List<string?"], -1, 0);

        Assert.Equal(0, result);
        Assert.Equal(["var fileContent = new List<string?>"], target);
    }

    [Fact]
    public void ApplyEdit_FragmentMatchWithIndentation_PreservesIndentation()
    {
        var target = new List<string>
        {
            "public void Method()", "{", "    var fileContent = new List<string>();", "}"
        };

        var result = EditApplier.ApplyEdit(target, ["fileContent = new List<string"], ["fileContent = new List<string?"], -1, 0);

        Assert.Equal(2, result);
        Assert.Equal(["public void Method()", "{", "    var fileContent = new List<string?>();", "}"], target);
    }

    [Fact]
    public void ApplyEdit_FragmentMatchCaseInsensitive_PreservesOriginalCase()
    {
        var target = new List<string> { "var x = new LIST<STRING>" };

        var result = EditApplier.ApplyEdit(target, ["list<string"], ["list<string?"], -1, 0);

        Assert.Equal(0, result);
        // The matched fragment (original case) is replaced; the untouched prefix keeps its case.
        Assert.Equal(["var x = new list<string?>"], target);
    }

    [Fact]
    public void ApplyEdit_FirstLineSuffixMatch_ReplacesOnlySuffix()
    {
        // First line truncated at the start: only the matched suffix is replaced.
        var target = new List<string>
        {
            "public void Method()", "{", "    var someLongVariable = ComputeValue();", "}"
        };
        var search = new List<string> { "ar someLongVariable = ComputeValue();", "}" };
        var replace = new List<string> { "ar someLongVariable = ComputeValue(1);", "}" };

        var result = EditApplier.ApplyEdit(target, search, replace, -1, 0);

        Assert.Equal(2, result);
        // Only the matched suffix was replaced — the leading "    v" of the original line is preserved.
        Assert.Equal(["public void Method()", "{", "    var someLongVariable = ComputeValue(1);", "}"], target);
    }

    [Fact]
    public void ApplyEdit_LastLinePrefixMatch_ReplacesOnlyPrefix()
    {
        // Last line truncated at the end: only the matched prefix is replaced,
        // the trailing part of the original line is preserved.
        var target = new List<string>
        {
            "public void Method()", "{", "    var x = 1;", "    DoSomething(value);", "}"
        };
        var search = new List<string> { "    var x = 1;", "    DoSomething(val" };
        var replace = new List<string> { "    var x = 1;", "    DoSomething(val" };

        var result = EditApplier.ApplyEdit(target, search, replace, -1, 0);

        Assert.Equal(2, result);
        // Only the matched prefix was replaced (identical replacement) — the trailing ")" of
        // the original line is preserved, so the line stays intact.
        Assert.Equal(["public void Method()", "{", "    var x = 1;", "    DoSomething(value);", "}"], target);
    }

    [Fact]
    public void ApplyEdit_ReplaceWithDifferentIndentation_KeepsReplacementIndentation()
    {
        // The model nests the replacement block differently — the replacement's own
        // indentation is kept verbatim when the match occupies a whole line.
        var target = new List<string> { "class A", "{", "    var x = 1;", "}" };
        var search = new List<string> { "    var x = 1;" };
        var replace = new List<string> { "        if (x)", "        {", "            DoWork();", "        }" };

        var result = EditApplier.ApplyEdit(target, search, replace, -1, 0);

        Assert.Equal(2, result);
        Assert.Equal(["class A", "{", "        if (x)", "        {", "            DoWork();", "        }", "}"], target);
    }

    [Fact]
    public void ApplyEdit_ExtraLinesAfterFragmentMatch_SuffixStaysOnLastReplacedLine()
    {
        // The suffix of the original line must stay attached to the last replaced line,
        // not silently move to the first extra replacement line.
        var target = new List<string> { "var f = new List<string>" };
        var search = new List<string> { "List<string" };
        var replace = new List<string> { "List<int>", "// note" };

        var result = EditApplier.ApplyEdit(target, search, replace, -1, 0);

        Assert.Equal(0, result);
        Assert.Equal(["var f = new List<int>", "// note>"], target);
    }

    [Fact]
    public void ApplyEdit_FewerReplaceLines_MultiLineWholeLineMatch_RemovesExtraSearchLines()
    {
        // Regression (todo.md: «если строк для замены меньше, чем заменяемых — заменяемые
        // строки изменяются на пустые строки вместо удаления»): replace-блок короче search-блока.
        // Лишние search-строки должны быть УДАЛЕНЫ (включая их line breaks), а не превращены
        // в пустые строки.
        var target = new List<string> { "a", "b", "c", "d" };

        var result = EditApplier.ApplyEdit(target, ["b", "c", "d"], ["B"], -1, 0);

        Assert.Equal(1, result);
        // b→B, c и d удалены целиком — никаких пустых строк вместо них.
        Assert.Equal(["a", "B"], target);
    }

    [Fact]
    public void ApplyEdit_FewerReplaceLines_MixedFragmentAndWholeLineMatch()
    {
        // Первая search-строка — whole-line match (заменяется verbatim), вторая — тоже
        // whole-line, но replace-блок короче: вторая строка должна быть удалена целиком,
        // включая её line break (не превращена в пустую строку).
        var target = new List<string> { "class A", "{", "    var x = 1;", "    var y = 2;", "}" };
        var search = new List<string> { "    var x = 1;", "    var y = 2;" };
        var replace = new List<string> { "    var x = 0;" };

        var result = EditApplier.ApplyEdit(target, search, replace, -1, 0);

        Assert.Equal(2, result);
        Assert.Equal(["class A", "{", "    var x = 0;", "}"], target);
    }

    [Fact]
    public void ApplyEdit_FewerReplaceLines_FragmentOnLastSearchLine_SuffixPreserved()
    {
        // Последняя search-строка — truncated boundary (префикс target-строки): при отсутствии
        // replacement для неё фрагмент удаляется, хвост строки (suffix) сохраняется —
        // удаляется только фрагмент, а не вся строка и не перевод строки.
        var target = new List<string> { "public void Method()", "{", "    var x = 1;", "    DoSomething(value);", "}" };
        var search = new List<string> { "    var x = 1;", "    DoSomething(val" };
        var replace = new List<string> { "    var x = 1;" };

        var result = EditApplier.ApplyEdit(target, search, replace, -1, 0);

        Assert.Equal(2, result);
        // search[1] без replacement: фрагмент «    DoSomething(val» удалён, suffix «ue);»
        // остался на последней эмитнутой строке (replace[0]).
        Assert.Equal(["public void Method()", "{", "    var x = 1;ue);", "}"], target);
    }

    [Fact]
    public void ApplyEdit_FewerReplaceLines_TrailingBlankLinesNotIntroduced()
    {
        // Модель заменяет блок из 3 строк одной строкой — лишние search-строки удаляются
        // без появления пустых строк в конце заменённого блока.
        var target = new List<string>
        {
            "public void Method()",
            "{",
            "    DoA();",
            "    DoB();",
            "    DoC();",
            "}"
        };
        var search = new List<string> { "    DoA();", "    DoB();", "    DoC();" };
        var replace = new List<string> { "    DoAll();" };

        var result = EditApplier.ApplyEdit(target, search, replace, -1, 0);

        Assert.Equal(2, result);
        Assert.Equal(["public void Method()", "{", "    DoAll();", "}"], target);
    }

    [Fact]
    public void ApplyEdit_NullArguments_ReturnsMinusOne()
    {
        Assert.Equal(-1, EditApplier.ApplyEdit(null!, ["a"], ["b"], -1, 0));
        Assert.Equal(-1, EditApplier.ApplyEdit(["a"], null, ["b"], -1, 0));
        Assert.Equal(-1, EditApplier.ApplyEdit(["a"], ["a"], null!, -1, 0));
    }

    [Fact]
    public void ApplyEdit_EmptyReplace_DeletesWholeBlock_SuffixPreserved()
    {
        // replace = []: весь сматченный блок удаляется, суффикс последней строки не теряется,
        // а прикрепляется к предыдущему тексту (или становится первой строкой).
        var target = new List<string> { "class A", "{", "    var x = 1;", "    DoSomething(value);", "}" };
        var search = new List<string> { "    var x = 1;", "    DoSomething(val" };
        var replace = new List<string>();

        var result = EditApplier.ApplyEdit(target, search, replace, -1, 0);

        Assert.Equal(2, result);
        // Обе строки удалены; суффикс «ue);» последней строки сохранён и прикреплён
        // к предыдущему тексту («{»).
        Assert.Equal(["class A", "{ue);", "}"], target);
    }

    [Fact]
    public void ApplyEdit_EmptyReplace_AtFileStart_SuffixBecomesFirstLine()
    {
        // Крайний случай: удаляемый блок начинается с первой строки файла — суффикс
        // последней строки становится первой строкой результата.
        var target = new List<string> { "    var x = 1;", "    DoSomething(value);", "}" };
        var search = new List<string> { "    var x = 1;", "    DoSomething(val" };
        var replace = new List<string>();

        var result = EditApplier.ApplyEdit(target, search, replace, -1, 0);

        Assert.Equal(0, result);
        Assert.Equal(["ue);", "}"], target);
    }

    [Fact]
    public void ApplyEdit_FewerReplaceLines_FragmentWithPrefix_PrefixPreserved()
    {
        // Фрагментная (boundary) строка без replacement: удаляется только фрагмент,
        // префикс строки (отступ) сохраняется — строка не исчезает целиком.
        var target = new List<string> { "public void Method()", "{", "    var x = 1;", "    DoSomething(value);", "}" };
        var search = new List<string> { "    var x = 1;", "    DoSomething(val" };
        var replace = new List<string> { "    var x = 2;" };

        var result = EditApplier.ApplyEdit(target, search, replace, -1, 0);

        Assert.Equal(2, result);
        // search[1] без replacement: фрагмент «    DoSomething(val» удалён, префикс
        // (отступ) сохранён, суффикс «ue);» прикреплён к последней эмитнутой строке.
        Assert.Equal(["public void Method()", "{", "    var x = 2;ue);", "}"], target);
    }

    [Fact]
    public void ApplyEdit_FirstLineSuffixMatch_RepeatedText_ReplacesRightmostOccurrence()
    {
        // Первая строка блока — suffix-матч: поисковый текст встречается в строке дважды.
        // Заменяться должен ПРАВЫЙ фрагмент (суффикс строки), а не первое вхождение.
        var target = new List<string> { "public void Method()", "{", "    Log(value = value;", "}" };
        var search = new List<string> { "value = value;", "}" };
        var replace = new List<string> { "value = 1;", "}" };

        var result = EditApplier.ApplyEdit(target, search, replace, -1, 0);

        Assert.Equal(2, result);
        // Суффикс «value = value;» заменён на «value = 1;», префикс «    Log(» сохранён.
        Assert.Equal(["public void Method()", "{", "    Log(value = 1;", "}"], target);
    }

    [Fact]
    public void FindInFile_RegexSpecialChars_InSearch_TreatedLiterally()
    {
        // Regex special characters in the search text must be treated literally.
        var target = new List<string> { "var re = new Regex(\"a+b\");", "var s = \"[test]$\";" };

        var result = EditApplier.FindInFile(target, ["a+b"], -1, 0);

        Assert.Equal(0, result);

        result = EditApplier.FindInFile(target, ["[test]$"], -1, 0);

        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_HintBeyondFile_FallsBackToFullSearch()
    {
        var target = new List<string> { "a", "b", "c" };

        var result = EditApplier.FindInFile(target, ["c"], 1000, 5);

        Assert.Equal(2, result);
    }

    [Fact]
    public void ApplyEdit_LastLinePrefixMatch_TrailingPartReplaced()
    {
        var target = new List<string>
        {
            "public void Method()", "{", "    var x = 1;", "    DoSomething(value);", "}"
        };
        var search = new List<string> { "    var x = 1;", "    DoSomething(val" };
        var replace = new List<string> { "    var x = 1;", "    Log(val" };

        var result = EditApplier.ApplyEdit(target, search, replace, -1, 0);

        Assert.Equal(2, result);
        // The matched prefix was replaced with "    Log(val" and the trailing "ue);" survived.
        Assert.Equal(["public void Method()", "{", "    var x = 1;", "    Log(value);", "}"], target);
    }

    [Fact]
    public void ApplyEdit_NoMatch_ReturnsMinusOne_AndDoesNotModify()
    {
        var target = new List<string> { "line1", "line2" };
        var original = target.ToArray();

        var result = EditApplier.ApplyEdit(target, ["notfound"], ["x"], -1, 0);

        Assert.Equal(-1, result);
        Assert.Equal(original, target);
    }

    [Fact]
    public void ApplyEdit_WithHint_UsesHint()
    {
        var target = new List<string> { "a", "b", "c" };

        var result = EditApplier.ApplyEdit(target, ["c"], ["C"], 3, 0);

        Assert.Equal(2, result);
        Assert.Equal(["a", "b", "C"], target);
    }

    [Fact]
    public void ApplyEdit_FragmentMatchInSecondEdit_SeesShiftedLines()
    {
        // Sequential edits: the second edit must see the file after the first edit.
        var target = new List<string>
        {
            "var a = new List<string>",
            "var b = new List<string>"
        };

        EditApplier.ApplyEdit(target, ["var a = new List<string"], ["var a = new List<string?>"], -1, 0);
        var result = EditApplier.ApplyEdit(target, ["var b = new List<string"], ["var b = new List<string?>"], -1, 0);

        Assert.Equal(1, result);
        Assert.Equal(["var a = new List<string?>", "var b = new List<string?>"], target);
    }

    #endregion
}
