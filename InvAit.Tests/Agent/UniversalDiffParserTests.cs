using InvAit.Agent;

namespace InvAit.Tests.Agent;

public class UniversalDiffParserTests
{
    private readonly UniversalDiffParser _parser = new();

    #region Basic Tests

    [Fact]
    public void FindInFile_ShouldFind_ExactMatch()
    {
        // Arrange
        var target = new List<string> { "bla bla" };
        var search = new List<string> { "bla bla" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_ShouldReturnMinusOne_WhenNotFound()
    {
        // Arrange
        var target = new List<string> { "bla bla", "foo bar" };
        var search = new List<string> { "not found" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_MultiLineSearch()
    {
        // Arrange
        var target = new List<string> { "line1", "line2", "line3", "line4" };
        var search = new List<string> { "line2", "line3" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(1, result);
    }

    #endregion

    #region Case Insensitivity Tests

    [Fact]
    public void FindInFile_ShouldFind_IgnoreCase()
    {
        // Arrange
        var target = new List<string> { "Hello World" };
        var search = new List<string> { "HELLO WORLD" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_MixedCase()
    {
        // Arrange
        var target = new List<string> { "HeLLo WoRLd" };
        var search = new List<string> { "hElLo wOrLd" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(0, result);
    }

    #endregion

    #region Trim Tests

    [Fact]
    public void FindInFile_ShouldFind_WithLeadingSpaces()
    {
        // Arrange
        var target = new List<string> { "   bla bla" };
        var search = new List<string> { "bla bla" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_WithTrailingSpaces()
    {
        // Arrange
        var target = new List<string> { "bla bla   " };
        var search = new List<string> { "bla bla" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_WithSpacesInBoth()
    {
        // Arrange
        var target = new List<string> { "  bla bla  " };
        var search = new List<string> { "   bla bla   " };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(0, result);
    }

    #endregion

    #region Hint Tests

    [Fact]
    public void FindInFile_ShouldFind_WithExactHint()
    {
        // Arrange
        var target = new List<string> { "line1", "line2", "line3", "line4" };
        var search = new List<string> { "line2", "line3" };

        // Act - hint is 1-based, line2 is at index 1 (0-based), so hint = 2
        var result = _parser.FindInFile(target, search, 2, 5);

        // Assert
        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_WithHintOffsetWithinTolerance()
    {
        // Arrange
        var target = new List<string> { "line1", "line2", "line3", "line4", "line5" };
        var search = new List<string> { "line3", "line4" };

        // Act - hint = 2 (expecting at index 1), but actual is at index 2, tol = 2
        var result = _parser.FindInFile(target, search, 2, 2);

        // Assert
        Assert.Equal(2, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_WithHintOffsetPositive()
    {
        // Arrange
        var target = new List<string> { "line1", "line2", "line3", "line4", "line5" };
        var search = new List<string> { "line4", "line5" };

        // Act - hint = 2, actual at index 3, tol = 5
        var result = _parser.FindInFile(target, search, 2, 5);

        // Assert
        Assert.Equal(3, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_WithHintOffsetNegative()
    {
        // Arrange
        var target = new List<string> { "line1", "line2", "line3", "line4", "line5" };
        var search = new List<string> { "line1", "line2" };

        // Act - hint = 3, actual at index 0, tol = 5
        var result = _parser.FindInFile(target, search, 3, 5);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_ShouldFallbackToFullSearch_WhenHintNotFoundWithinTolerance()
    {
        // Arrange
        var target = new List<string> { "line1", "line2", "line3", "line4", "line5" };
        var search = new List<string> { "line4", "line5" };

        // Act - hint = 1, actual at index 3, tol = 1 (too small)
        var result = _parser.FindInFile(target, search, 1, 1);

        // Assert - should fallback to full search and find at index 3
        Assert.Equal(3, result);
    }

    #endregion

    #region Multiple Similar Strings Tests

    [Fact]
    public void FindInFile_ShouldFindFirst_WhenMultipleSimilarStringsWithDifferentLastLine()
    {
        // Arrange - критический тест: несколько похожих блоков, различие в последней строке
        var target = new List<string>
        {
            "public void Method()",
            "{",
            "    var x = 1;",
            "}",
            "public void Method()",
            "{",
            "    var x = 2;",  // различие здесь
            "}"
        };
        var search = new List<string>
        {
            "public void Method()",
            "{",
            "    var x = 1;",
            "}"
        };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert - должен найти первое вхождение (индекс 0)
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_ShouldFindSecond_WhenSearchingForSecondSimilarBlock()
    {
        // Arrange
        var target = new List<string>
        {
            "public void Method()",
            "{",
            "    var x = 1;",
            "}",
            "public void Method()",
            "{",
            "    var x = 2;",  // различие здесь
            "}"
        };
        var search = new List<string>
        {
            "public void Method()",
            "{",
            "    var x = 2;",
            "}"
        };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert - должен найти второе вхождение (индекс 4)
        Assert.Equal(4, result);
    }

    [Fact]
    public void FindInFile_ShouldFindCorrect_WhenMultipleSimilarSingleLinesWithDifferentEndings()
    {
        // Arrange
        var target = new List<string>
        {
            "Console.WriteLine(\"Hello\");",
            "Console.WriteLine(\"Hello!\");",
            "Console.WriteLine(\"Hello?\");"
        };
        var search = new List<string> { "Console.WriteLine(\"Hello!\");" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_ShouldFindFirst_WhenMultipleIdenticalBlocks()
    {
        // Arrange - несколько идентичных блоков
        var target = new List<string>
        {
            "line1",
            "line2",
            "line1",
            "line2"
        };
        var search = new List<string> { "line1", "line2" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert - должен вернуть первое вхождение
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_ShouldFindCorrectBlock_WhenOverlappingPatterns()
    {
        // Arrange - перекрывающиеся паттерны
        var target = new List<string>
        {
            "A",
            "A",
            "B"
        };
        var search = new List<string> { "A", "B" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert - должен найти A, B начиная с индекса 1
        Assert.Equal(1, result);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void FindInFile_ShouldReturnMinusOne_WhenSearchLongerThanTarget()
    {
        // Arrange
        var target = new List<string> { "line1" };
        var search = new List<string> { "line1", "line2" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_ShouldFindAtEnd()
    {
        // Arrange
        var target = new List<string> { "line1", "line2", "line3" };
        var search = new List<string> { "line2", "line3" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_WhenHintIsNegativeOne()
    {
        // Arrange
        var target = new List<string> { "line1", "line2", "line3" };
        var search = new List<string> { "line2" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_ShouldHandleHintAtStart()
    {
        // Arrange
        var target = new List<string> { "line1", "line2", "line3" };
        var search = new List<string> { "line1" };

        // Act - hint = 1 (first line)
        var result = _parser.FindInFile(target, search, 1, 5);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_ShouldHandleHintAtEnd()
    {
        // Arrange
        var target = new List<string> { "line1", "line2", "line3" };
        var search = new List<string> { "line3" };

        // Act - hint = 3 (last line, 1-based)
        var result = _parser.FindInFile(target, search, 3, 5);

        // Assert
        Assert.Equal(2, result);
    }

    #endregion

    #region Null and Empty Tests

    [Fact]
    public void FindInFile_ShouldReturnMinusOne_WhenTargetIsNull()
    {
        // Arrange
        List<string> target = null;
        var search = new List<string> { "line1" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_ShouldReturnMinusOne_WhenSearchIsNull()
    {
        // Arrange
        var target = new List<string> { "line1" };
        List<string> search = null;

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_ShouldReturnMinusOne_WhenBothNull()
    {
        // Act
        var result = _parser.FindInFile(null, null, -1, 5);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_ShouldReturnMinusOne_WhenSearchIsEmpty()
    {
        // Arrange
        var target = new List<string> { "line1", "line2" };
        var search = new List<string>();

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_ShouldReturnMinusOne_WhenTargetIsEmpty()
    {
        // Arrange
        var target = new List<string>();
        var search = new List<string> { "line1" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindInFile_ShouldHandleNegativeTolerance()
    {
        // Arrange
        var target = new List<string> { "line1", "line2", "line3" };
        var search = new List<string> { "line2" };

        // Act - negative tolerance should be treated as 0
        var result = _parser.FindInFile(target, search, 2, -5);

        // Assert - should still find via full search fallback
        Assert.Equal(1, result);
    }

    #endregion

    #region Empty Lines in Target Tests

    [Fact]
    public void FindInFile_ShouldFind_WithEmptyLinesInTarget()
    {
        // Arrange
        var target = new List<string>
        {
            "line1",
            "",
            "line2",
            "",
            "line3"
        };
        var search = new List<string> { "line2" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(2, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_WhenSearchContainsEmptyLines()
    {
        // Arrange
        var target = new List<string>
        {
            "line1",
            "",
            "line2",
            "line3"
        };
        var search = new List<string>
        {
            "line1",
            "",
            "line2"
        };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_WithMultipleEmptyLinesInTarget()
    {
        // Arrange
        var target = new List<string>
        {
            "line1",
            "",
            "",
            "",
            "line2"
        };
        var search = new List<string>
        {
            "",
            "",
            "line2"
        };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(2, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_WithEmptyLinesAtStart()
    {
        // Arrange
        var target = new List<string>
        {
            "",
            "",
            "line1",
            "line2"
        };
        var search = new List<string> { "line1" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(2, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_WithEmptyLinesAtEnd()
    {
        // Arrange
        var target = new List<string>
        {
            "line1",
            "line2",
            "",
            ""
        };
        var search = new List<string> { "line2" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_WithOnlyEmptyLinesInSearch()
    {
        // Arrange
        var target = new List<string>
        {
            "line1",
            "",
            "",
            "line2"
        };
        var search = new List<string> { "", "" };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(1, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_WithWhitespaceOnlyLines()
    {
        // Arrange - строки только с пробелами должны восприниматься как пустые после Trim
        var target = new List<string>
        {
            "line1",
            "   ",  // только пробелы
            "line2"
        };
        var search = new List<string>
        {
            "line1",
            "",  // пустая строка должна совпасть с "   " после Trim
            "line2"
        };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindInFile_ShouldFind_WithHintAndEmptyLines()
    {
        // Arrange
        var target = new List<string>
        {
            "line1",
            "",
            "line2",
            "",
            "line3"
        };
        var search = new List<string> { "line3" };

        // Act - hint = 5 (1-based), line3 is at index 4
        var result = _parser.FindInFile(target, search, 5, 2);

        // Assert
        Assert.Equal(4, result);
    }

    [Fact]
    public void FindInFile_ShouldDistinguishEmptyLinesFromContent()
    {
        // Arrange - проверяем что пустые строки не путаются с непустыми
        var target = new List<string>
        {
            "public void Method()",
            "{",
            "",  // пустая строка внутри метода
            "    var x = 1;",
            "}",
            "public void Method()",
            "{",
            "    var x = 2;",  // без пустой строки
            "}"
        };
        var search = new List<string>
        {
            "public void Method()",
            "{",
            "    var x = 2;",
            "}"
        };

        // Act
        var result = _parser.FindInFile(target, search, -1, 5);

        // Assert - должен найти второй блок (индекс 5)
        Assert.Equal(5, result);
    }

    #endregion
}
