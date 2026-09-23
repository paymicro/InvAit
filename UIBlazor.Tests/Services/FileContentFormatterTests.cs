namespace UIBlazor.Tests.Services;

using Shared.Contracts;

/// <summary>
/// Tests for <seealso cref="FileContentFormatter"/>.
/// </summary>
public class FileContentFormatterTests
{
    #region TryDeserialize

    [Fact]
    public void TryDeserialize_EmptyContent_ReturnsNull()
    {
        Assert.Null(FileContentFormatter.TryDeserialize(""));
        Assert.Null(FileContentFormatter.TryDeserialize(null!));
    }

    [Fact]
    public void TryDeserialize_NonJsonContent_ReturnsNull()
    {
        Assert.Null(FileContentFormatter.TryDeserialize("Just plain text"));
        Assert.Null(FileContentFormatter.TryDeserialize("### Program.cs\n```\ncode\n```"));
    }

    [Fact]
    public void TryDeserialize_JsonArray_ReturnsFileContents()
    {
        var json = """[{"path":"Program.cs","lines":["using System;","var x = 1;"]}]""";

        var result = FileContentFormatter.TryDeserialize(json);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Program.cs", result![0].Path);
        Assert.Equal(2, result[0].Lines.Count);
        Assert.Equal("using System;", result[0].Lines[0]);
    }

    [Fact]
    public void TryDeserialize_WithOptionalFields_ReturnsFileContents()
    {
        var json = """[{"path":"Big.cs","lines":["line1","line2"],"totalLines":500,"startLine":10}]""";

        var result = FileContentFormatter.TryDeserialize(json);

        Assert.NotNull(result);
        Assert.Equal(500, result![0].TotalLines);
        Assert.Equal(10, result[0].StartLine);
    }

    [Fact]
    public void TryDeserialize_WithErrorField_ReturnsFileContents()
    {
        var json = """[{"path":"missing.cs","lines":[],"error":"File doesn't exist."}]""";

        var result = FileContentFormatter.TryDeserialize(json);

        Assert.NotNull(result);
        Assert.Equal("File doesn't exist.", result![0].Error);
    }

    [Fact]
    public void TryDeserialize_EmptyPathAndLines_ReturnsNull()
    {
        // JSON array that deserializes but no item has valid Path or Lines
        var json = """[{"path":"","lines":[]}]""";

        var result = FileContentFormatter.TryDeserialize(json);

        Assert.Null(result);
    }

    [Fact]
    public void TryDeserialize_NonFileContentJsonArray_ReturnsNull()
    {
        // Valid JSON array but not FileContent shape
        var json = """[{"name":"foo","value":"bar"}]""";

        var result = FileContentFormatter.TryDeserialize(json);

        Assert.Null(result);
    }

    #endregion

    #region Format

    [Fact]
    public void Format_SingleFile_AddsLineNumbers()
    {
        var files = new List<FileContent>
        {
            new() { Path = "Program.cs", Lines = ["using System;", "var x = 1;"] }
        };

        var result = FileContentFormatter.Format(files);

        Assert.Contains("### Program.cs", result);
        Assert.Contains("```", result);
        Assert.Contains("   1 | using System;", result);
        Assert.Contains("   2 | var x = 1;", result);
    }

    [Fact]
    public void Format_MultipleFiles_AddsSeparator()
    {
        var files = new List<FileContent>
        {
            new() { Path = "File1.cs", Lines = ["line1"] },
            new() { Path = "File2.cs", Lines = ["lineA"] }
        };

        var result = FileContentFormatter.Format(files);

        Assert.Contains("### File1.cs", result);
        Assert.Contains("### File2.cs", result);
        Assert.Contains("---", result);
        Assert.Contains("   1 | line1", result);
        Assert.Contains("   1 | lineA", result);
    }

    [Fact]
    public void Format_WithError_ShowsErrorWithoutCodeBlock()
    {
        var files = new List<FileContent>
        {
            new() { Path = "missing.cs", Lines = [], Error = "File doesn't exist." }
        };

        var result = FileContentFormatter.Format(files);

        Assert.Contains("### missing.cs", result);
        Assert.Contains("File doesn't exist.", result);
        // Should not have a code block for error files
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void Format_WithNullLines_ShowsEmptyCodeBlock()
    {
        var files = new List<FileContent>
        {
            new() { Path = "empty.cs", Lines = null! }
        };

        var result = FileContentFormatter.Format(files);

        Assert.Contains("### empty.cs", result);
        Assert.Contains("```", result);
        // Should not crash — just empty code block
    }

    [Fact]
    public void Format_WithTotalLinesButNoTruncation_DoesNotShowNotice()
    {
        // TotalLines equals Lines.Count — file was read to the end
        var files = new List<FileContent>
        {
            new() { Path = "full.cs", Lines = ["line1", "line2"], TotalLines = 2 }
        };

        var result = FileContentFormatter.Format(files);

        Assert.DoesNotContain("File has", result);
        Assert.DoesNotContain("startLine=", result);
    }

    [Fact]
    public void Format_WithStartLine_NumbersFromStartLine()
    {
        var files = new List<FileContent>
        {
            new() { Path = "Big.cs", Lines = ["line10", "line11"], StartLine = 10 }
        };

        var result = FileContentFormatter.Format(files);

        Assert.Contains("  10 | line10", result);
        Assert.Contains("  11 | line11", result);
    }

    [Fact]
    public void Format_WithTruncation_ShowsNotice()
    {
        var files = new List<FileContent>
        {
            new() { Path = "Big.cs", Lines = ["line1", "line2"], TotalLines = 500 }
        };

        var result = FileContentFormatter.Format(files);

        Assert.Contains("File has 500 lines total", result);
        Assert.Contains("Showing lines 1-2", result);
        Assert.Contains("startLine=3", result);
    }

    [Fact]
    public void Format_WithTruncationAndStartLine_ShowsCorrectNotice()
    {
        var files = new List<FileContent>
        {
            new() { Path = "Big.cs", Lines = ["line10", "line11"], StartLine = 10, TotalLines = 500 }
        };

        var result = FileContentFormatter.Format(files);

        Assert.Contains("Showing lines 10-11", result);
        Assert.Contains("startLine=12", result);
    }

    #endregion

    #region FormatContent (integration)

    [Fact]
    public void FormatContent_JsonContent_FormatsWithLineNumbers()
    {
        var json = """[{"path":"Program.cs","lines":["using System;","var x = 1;"]}]""";

        var result = FileContentFormatter.FormatContent(json);

        Assert.Contains("### Program.cs", result);
        Assert.Contains("   1 | using System;", result);
        Assert.Contains("   2 | var x = 1;", result);
    }

    [Fact]
    public void FormatContent_RawText_ReturnsAsIs()
    {
        var content = "Just plain text from bash output";

        var result = FileContentFormatter.FormatContent(content);

        Assert.Equal(content, result);
    }

    [Fact]
    public void FormatContent_OldMarkdownFormat_ReturnsAsIs()
    {
        // Old sessions in localStorage may have this format
        var content = "### Program.cs\n```\nusing System;\n```";

        var result = FileContentFormatter.FormatContent(content);

        Assert.Equal(content, result);
    }

    #endregion

    #region Filter + Format pipeline

    [Fact]
    public void Pipeline_FilterThenFormat_StripsCommentsAndNumbers()
    {
        var json = """[{"path":"Program.cs","lines":["using System;","// this is a comment","var x = 1;"]}]""";

        var filter = new ContentFilterService(Mock.Of<ICommonSettingsProvider>(
            x => x.Current == new CommonOptions
            {
                ContentFilter = new ContentFilterSettings
                {
                    IsEnabled = true,
                    StripComments = true,
                    StripBlankLines = false,
                    TrimWhitespace = false
                }
            }));

        var filtered = filter.Filter(json, "read_files");

        // Comment should be removed, code preserved with line numbers
        Assert.DoesNotContain("// this is a comment", filtered);
        Assert.Contains("using System;", filtered);
        Assert.Contains("var x = 1;", filtered);
        // Line numbers should be sequential (1, 2) — comment removed
        Assert.Contains("   1 | using System;", filtered);
        Assert.Contains("   2 | var x = 1;", filtered);
    }

    [Fact]
    public void Pipeline_FilterStripsBlankLines_Renumbers()
    {
        var json = """[{"path":"Program.cs","lines":["using System;","","","var x = 1;"]}]""";

        var filter = new ContentFilterService(Mock.Of<ICommonSettingsProvider>(
            x => x.Current == new CommonOptions
            {
                ContentFilter = new ContentFilterSettings
                {
                    IsEnabled = true,
                    StripComments = false,
                    StripBlankLines = true,
                    TrimWhitespace = false
                }
            }));

        var filtered = filter.Filter(json, "read_files");

        // Blank lines removed, code renumbered
        Assert.Contains("   1 | using System;", filtered);
        Assert.Contains("   2 | var x = 1;", filtered);
    }

    #endregion
}
