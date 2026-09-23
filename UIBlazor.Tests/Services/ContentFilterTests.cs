namespace UIBlazor.Tests.Services;

/// <summary>
/// Tests for <seealso cref="ContentFilterService"/>.
/// </summary>
public class ContentFilterTests
{
    private readonly Mock<ICommonSettingsProvider> _mockSettings;
    private readonly CommonOptions _options;

    public ContentFilterTests()
    {
        _mockSettings = new Mock<ICommonSettingsProvider>();
        _options = new CommonOptions();
        _mockSettings.Setup(x => x.Current).Returns(_options);
    }

    private ContentFilterService CreateFilter(ContentFilterSettings? settings = null)
    {
        _options.ContentFilter = settings ?? new ContentFilterSettings();
        return new ContentFilterService(_mockSettings.Object);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  IsActive
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void IsActive_Disabled_ReturnsFalse()
    {
        // Arrange
        var filter = CreateFilter(new ContentFilterSettings { IsEnabled = false });

        // Act & Assert
        Assert.False(filter.IsActive);
    }

    [Fact]
    public void IsActive_EnabledWithPresets_ReturnsTrue()
    {
        // Arrange
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = false,
            TrimWhitespace = false
        });

        // Act & Assert
        Assert.True(filter.IsActive);
    }

    [Fact]
    public void IsActive_EnabledWithOnlyCustomRules_ReturnsTrue()
    {
        // Arrange
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = false,
            StripBlankLines = false,
            TrimWhitespace = false,
            CustomRules =
            [
                new ContentFilterRule { Pattern = "foo", IsEnabled = true }
            ]
        });

        // Act & Assert
        Assert.True(filter.IsActive);
    }

    [Fact]
    public void IsActive_EnabledButNoActiveRules_ReturnsFalse()
    {
        // Arrange
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = false,
            StripBlankLines = false,
            TrimWhitespace = false,
            CustomRules =
            [
                new ContentFilterRule { Pattern = "foo", IsEnabled = false }
            ]
        });

        // Act & Assert
        Assert.False(filter.IsActive);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Disabled / empty
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Filter_Disabled_ReturnsOriginalContent_ForRawText()
    {
        // Arrange — raw text (not structured JSON) is returned as-is when filter is disabled
        var content = "var x = 1; // comment\n";
        var filter = CreateFilter(new ContentFilterSettings { IsEnabled = false });

        // Act
        var result = filter.Filter(content, "read_file");

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public void Filter_Disabled_FormatsStructuredJson_ForLLM()
    {
        // When filter is disabled, structured FileContent JSON should still be
        // formatted into human-readable text with line numbers for the LLM.
        var json = """[{"path":"test.cs","lines":["var x = 1;","var y = 2;"]}]""";
        var filter = CreateFilter(new ContentFilterSettings { IsEnabled = false });

        // Act
        var result = filter.Filter(json, "read_files");

        // Assert — formatted with line numbers, not raw JSON
        Assert.Contains("### test.cs", result);
        Assert.Contains("   1 | var x = 1;", result);
        Assert.Contains("   2 | var y = 2;", result);
        Assert.DoesNotContain("[{\"path\":", result);
    }

    [Fact]
    public void EmptyContent_ReturnsEmpty()
    {
        // Arrange
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true
        });

        // Act
        var result = filter.Filter("", "read_file");

        // Assert
        Assert.Equal("", result);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  StripComments
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void StripComments_RemovesSingleLineComments()
    {
        // Structured JSON format — clean lines, no line numbers
        var json = """[{"path":"test.cs","lines":["var x = 1;","// this is a comment","var y = 2;"]}]""";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = false,
            TrimWhitespace = false
        });

        // Act
        var result = filter.Filter(json, "read_files");

        // Assert
        Assert.DoesNotContain("// this is a comment", result);
        Assert.Contains("var x = 1;", result);
        Assert.Contains("var y = 2;", result);
    }

    [Fact]
    public void StripComments_RemovesMultiLineComments()
    {
        var json = """[{"path":"test.cs","lines":["var x = 1;","/* multi","line","comment */","var y = 2;"]}]""";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = false,
            TrimWhitespace = false
        });

        // Act
        var result = filter.Filter(json, "read_files");

        // Assert
        Assert.DoesNotContain("/* multi", result);
        Assert.DoesNotContain("comment */", result);
        Assert.Contains("var x = 1;", result);
        Assert.Contains("var y = 2;", result);
    }

    [Fact]
    public void StripComments_RemovesHashComments_ButPreservesShebang()
    {
        // Python file — # is a comment
        var json = """[{"path":"script.py","lines":["#!/usr/bin/env python","# regular comment","x = 1"]}]""";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = false,
            TrimWhitespace = false
        });

        // Act
        var result = filter.Filter(json, "read_files");

        // Assert
        Assert.Contains("#!/usr/bin/env python", result);
        Assert.DoesNotContain("# regular comment", result);
        Assert.Contains("x = 1", result);
    }

    [Fact]
    public void StripComments_RemovesSqlComments()
    {
        var json = """[{"path":"query.sql","lines":["SELECT * FROM users;","-- this is a sql comment","WHERE id = 1;"]}]""";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = false,
            TrimWhitespace = false
        });

        // Act
        var result = filter.Filter(json, "read_files");

        // Assert
        Assert.DoesNotContain("-- this is a sql comment", result);
        Assert.Contains("SELECT * FROM users;", result);
        Assert.Contains("WHERE id = 1;", result);
    }

    [Fact]
    public void StripComments_DoesNotStripDashInNonSqlFiles()
    {
        // .cs file — -- is NOT a comment
        var json = """[{"path":"test.cs","lines":["var x = 1;","-- some text","var y = 2;"]}]""";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = false,
            TrimWhitespace = false
        });

        // Act
        var result = filter.Filter(json, "read_files");

        // Assert — -- line preserved for non-SQL files
        Assert.Contains("-- some text", result);
        Assert.Contains("var x = 1;", result);
        Assert.Contains("var y = 2;", result);
    }

    [Fact]
    public void StripComments_PreservesHashInUnknownExtension()
    {
        // Unknown extension — # is NOT stripped (safe default)
        var json = """[{"path":"readme.txt","lines":["# heading","some text"]}]""";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = false,
            TrimWhitespace = false
        });

        // Act
        var result = filter.Filter(json, "read_files");

        // Assert — # preserved for unknown extensions
        Assert.Contains("# heading", result);
        Assert.Contains("some text", result);
    }

    [Fact]
    public void Filter_StructuredFileContent_PassesErrorThrough()
    {
        // FileContent with Error should be passed through without filtering
        var json = """[{"path":"missing.cs","lines":[],"error":"File doesn't exist."}]""";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = true,
            TrimWhitespace = true
        });

        // Act
        var result = filter.Filter(json, "read_files");

        // Assert — error is shown, no crash
        Assert.Contains("### missing.cs", result);
        Assert.Contains("File doesn't exist.", result);
    }

    [Fact]
    public void Filter_StructuredFileContent_NullLines_DoesNotCrash()
    {
        // FileContent with null Lines (from JSON where lines field is null)
        var json = """[{"path":"empty.cs","lines":null}]""";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = true,
            TrimWhitespace = true
        });

        // Act
        var result = filter.Filter(json, "read_files");

        // Assert — no crash, shows empty code block
        Assert.Contains("### empty.cs", result);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  StripBlankLines
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void StripBlankLines_RemovesEmptyLines()
    {
        // Arrange
        var content = "line1\n\n\nline2\n   \nline3\n";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = false,
            StripBlankLines = true,
            TrimWhitespace = false
        });

        // Act
        var result = filter.Filter(content, "read_file");

        // Assert
        Assert.Contains("line1", result);
        Assert.Contains("line2", result);
        Assert.Contains("line3", result);
        // The blank lines between line1 and line2 should be gone
        Assert.DoesNotContain("\n\n\n", result);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TrimWhitespace
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void TrimWhitespace_RemovesTrailingWhitespace()
    {
        // Arrange
        var content = "line1   \nline2\t\nline3\n";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = false,
            StripBlankLines = false,
            TrimWhitespace = true
        });

        // Act
        var result = filter.Filter(content, "read_file");

        // Assert
        Assert.DoesNotContain("line1   ", result);
        Assert.DoesNotContain("line2\t", result);
        Assert.Contains("line1\n", result);
        Assert.Contains("line2\n", result);
    }

    [Fact]
    public void TrimWhitespace_CollapsesMultipleBlankLines()
    {
        // Arrange
        var content = "line1\n\n\n\n\nline2\n";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = false,
            StripBlankLines = false,
            TrimWhitespace = true
        });

        // Act
        var result = filter.Filter(content, "read_file");

        // Assert
        // 3+ consecutive newlines should be collapsed to 2 (one blank line)
        Assert.DoesNotContain("\n\n\n", result);
        Assert.Contains("line1\n\nline2", result);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Custom rules
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void CustomRule_AppliesRegex()
    {
        // Arrange
        var content = "hello world hello";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = false,
            StripBlankLines = false,
            TrimWhitespace = false,
            CustomRules =
            [
                new ContentFilterRule
                {
                    Name = "replace-hello",
                    Pattern = @"hello",
                    Replacement = "bye",
                    IsEnabled = true
                }
            ]
        });

        // Act
        var result = filter.Filter(content, "read_file");

        // Assert
        Assert.Equal("bye world bye", result);
    }

    [Fact]
    public void CustomRule_Disabled_NotApplied()
    {
        // Arrange
        var content = "hello world";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = false,
            StripBlankLines = false,
            TrimWhitespace = false,
            CustomRules =
            [
                new ContentFilterRule
                {
                    Name = "replace-hello",
                    Pattern = @"hello",
                    Replacement = "bye",
                    IsEnabled = false
                }
            ]
        });

        // Act
        var result = filter.Filter(content, "read_file");

        // Assert
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void CustomRule_InvalidRegex_DoesNotCrash()
    {
        // Arrange
        var content = "hello world";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = false,
            StripBlankLines = false,
            TrimWhitespace = false,
            CustomRules =
            [
                new ContentFilterRule
                {
                    Name = "bad-regex",
                    Pattern = @"[invalid(",
                    Replacement = "bye",
                    IsEnabled = true
                }
            ]
        });

        // Act — should not throw, invalid pattern is skipped during cache build
        var result = filter.Filter(content, "read_file");

        // Assert
        Assert.Equal("hello world", result);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Combined presets
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void MultiplePresets_AppliedTogether()
    {
        // Structured JSON with comments, whitespace, and blank lines
        var json = """[{"path":"test.cs","lines":["var x = 1;   ","// comment line","/* block comment */","var y = 2;","","","var z = 3;"]}]""";

        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = true,
            TrimWhitespace = true
        });

        // Act
        var result = filter.Filter(json, "read_files");

        // Assert — comments removed
        Assert.DoesNotContain("// comment line", result);
        Assert.DoesNotContain("/* block comment */", result);
        // Trailing whitespace removed
        Assert.DoesNotContain("var x = 1;   ", result);
        // Code preserved
        Assert.Contains("var x = 1;", result);
        Assert.Contains("var y = 2", result);
        Assert.Contains("var z = 3;", result);
        // No 3+ consecutive newlines
        Assert.DoesNotContain("\n\n\n", result);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Structured FileContent JSON (new format from ToolExecutor)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Filter_StructuredFileContent_StripsCommentsFromLines()
    {
        // New format: JSON array of FileContent with clean lines (no line numbers, no markdown)
        var json = """[{"path":"Program.cs","lines":["using System;","// this is a comment","var x = 1;"]}]""";

        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = false,
            TrimWhitespace = false
        });

        var result = filter.Filter(json, "read_files");

        // Comment line removed, code preserved
        Assert.DoesNotContain("// this is a comment", result);
        Assert.Contains("using System;", result);
        Assert.Contains("var x = 1;", result);
        // Output is formatted with line numbers (sequential after comment removal)
        Assert.Contains("### Program.cs", result);
        Assert.Contains("   1 | using System;", result);
        Assert.Contains("   2 | var x = 1;", result);
    }

    [Fact]
    public void StripComments_PreservesCPreprocessorDirectives()
    {
        // C file — #include, #define are NOT comments
        var json = """[{"path":"main.c","lines":["#include <stdio.h>","#define PI 3.14","int main() {"]}]""";
        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = false,
            TrimWhitespace = false
        });

        // Act
        var result = filter.Filter(json, "read_files");

        // Assert — # lines preserved for C/C++ files
        Assert.Contains("#include <stdio.h>", result);
        Assert.Contains("#define PI 3.14", result);
        Assert.Contains("int main() {", result);
    }

    [Fact]
    public void Filter_StructuredFileContent_MultipleFiles()
    {
        var json = """[{"path":"File1.cs","lines":["// comment","code1"]},{"path":"File2.py","lines":["# comment","code2"]}]""";

        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = false,
            TrimWhitespace = false
        });

        var result = filter.Filter(json, "read_files");

        Assert.Contains("### File1.cs", result);
        Assert.Contains("### File2.py", result);
        Assert.DoesNotContain("// comment", result);
        Assert.DoesNotContain("# comment", result);
        Assert.Contains("code1", result);
        Assert.Contains("code2", result);
    }

    [Fact]
    public void Filter_RawText_FallbackDoesNotStripComments()
    {
        // Non-file tools (bash output, etc.) — comment stripping is NOT applied
        // because file extension is unknown in raw text mode.
        var content = "line1\n// comment\nline2\n";

        var filter = CreateFilter(new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = true,
            StripBlankLines = false,
            TrimWhitespace = false
        });

        var result = filter.Filter(content, "bash");

        // Comments are preserved in raw text mode
        Assert.Contains("// comment", result);
        Assert.Contains("line1", result);
        Assert.Contains("line2", result);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Regex cache reuse
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void CustomRule_CacheReusesRegex()
    {
        // Arrange
        var settings = new ContentFilterSettings
        {
            IsEnabled = true,
            StripComments = false,
            StripBlankLines = false,
            TrimWhitespace = false,
            CustomRules =
            [
                new ContentFilterRule
                {
                    Name = "replace-foo",
                    Pattern = @"foo",
                    Replacement = "bar",
                    IsEnabled = true
                }
            ]
        };
        var filter = CreateFilter(settings);
        var content = "foo baz foo";

        // Act — call Filter twice with the same rules; the regex cache should be reused
        var result1 = filter.Filter(content, "read_file");
        var result2 = filter.Filter(content, "read_file");

        // Assert — both calls produce the same correct result without crashing
        Assert.Equal("bar baz bar", result1);
        Assert.Equal("bar baz bar", result2);
    }
}
