using System.Collections.Generic;
using System.Text.Json;
using Shared.Contracts;
using ToolCore;
using Xunit;

namespace ToolCore.Tests;

/// <summary>
/// The shape rules of `read_files`, pinned as SENTENCES — the refusal is the only thing the model
/// sees of a rejected call, so each case asserts the whole text it will read, not a boolean.
///
/// Every payload here goes through the SAME entry point a real call uses
/// (<c>ReadFilesPayload.TryParse</c> with the raw payload value), and the assertions use the exact
/// words <c>VSCode/src/toolHandlers/fileOps.ts</c> produces for the same payload.
/// </summary>
public class ReadFilesPayloadTests
{
    private const string RefusedNothingRead = "Nothing was read.";

    private static object? Files(string filesJson)
    {
        using var doc = JsonDocument.Parse(filesJson);
        return doc.RootElement.Clone();
    }

    private static bool Parse(string filesJson, out List<ReadFileParams> files, out string? refusal)
        => ReadFilesPayload.TryParse(Files(filesJson), out files, out refusal);

    // ── the array itself ─────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_MissingFiles_AsksForTheArrayWithAnExample()
    {
        Assert.False(ReadFilesPayload.TryParse(null, out var files, out var refusal));

        Assert.Empty(files);
        Assert.Equal(
            "Failed to read 'files': the value is missing. Send a JSON array of reads, " +
            "e.g. [{\"path\": \"src/File.cs\"}]. Nothing was read.",
            refusal);
    }

    [Theory]
    [InlineData("\"oops\"", "the string \"oops\"")]
    [InlineData("{}", "an object {}")]
    [InlineData("7", "the number 7")]
    public void TryParse_FilesThatIsNotAnArray_NamesTheKindItReceived(string json, string described)
    {
        Assert.False(Parse(json, out var files, out var refusal));

        Assert.Empty(files);
        Assert.Equal($"Failed to read 'files': expected a JSON array, received {described}. {RefusedNothingRead}", refusal);
    }

    [Fact]
    public void TryParse_EmptyArray_IsAnEmptyPlan_NotAParseFailure()
    {
        // An empty array is a legal (empty) request; the host words it "No files specified.".
        Assert.True(Parse("[]", out var files, out var refusal));

        Assert.Null(refusal);
        Assert.Empty(files);
    }

    // ── one entry ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("[{\"path\":\"a.cs\"},null]", "entry 2 of 2 must be a JSON object with a 'path', received null")]
    [InlineData("[\"a.cs\"]", "entry 1 of 1 must be a JSON object with a 'path', received the string \"a.cs\"")]
    [InlineData("[7]", "entry 1 of 1 must be a JSON object with a 'path', received the number 7")]
    [InlineData("[[\"a.cs\"]]", "entry 1 of 1 must be a JSON object with a 'path', received an array [\"a.cs\"]")]
    public void TryParse_EntryThatIsNotAnObject_IsRefusedWithItsPosition(string json, string expectedBody)
    {
        Assert.False(Parse(json, out var files, out var refusal));

        Assert.Empty(files);
        Assert.Equal($"Failed to read 'files': {expectedBody}. {RefusedNothingRead}", refusal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"path\":null}")]
    [InlineData("{\"path\":\"\"}")]
    [InlineData("{\"path\":\"   \"}")]
    [InlineData("{\"path\":42}")]
    [InlineData("{\"Path\":\"a.cs\",\"path\":null}")]
    public void TryParse_EntryWithoutAUsablePath_IsRefusedByPosition(string json)
    {
        // A blank path is the same mistake as a missing one: the host resolved it to the solution
        // root and answered "Path cannot be null or empty." from OUTSIDE its own error handling,
        // which destroyed the rest of the batch.
        Assert.False(Parse($"[{json}]", out var files, out var refusal));

        Assert.Empty(files);
        Assert.Equal(
            "Failed to read 'files': entry 1 of 1 has no 'path'. Each entry needs a 'path' string. Nothing was read.",
            refusal);
    }

    [Fact]
    public void TryParse_RefusalNamesTheBrokenEntryInASecondOne()
    {
        Assert.False(Parse("[{\"path\":\"a.cs\"},{\"path\":\"b.cs\",\"startLine\":\"2\"}]", out _, out var refusal));

        Assert.Equal(
            "Failed to read 'files': entry 2 of 2: 'startLine' must be an integer or null, " +
            "received the string \"2\". Nothing was read.",
            refusal);
    }

    // ── a range ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"3\"", "the string \"3\"")]
    [InlineData("1.5", "the number 1.5")]
    [InlineData("true", "the boolean true")]
    [InlineData("{}", "an object {}")]
    [InlineData("[]", "an array []")]
    public void TryParse_RangeOfAnotherType_IsRefusedInsteadOfFoldedToNoRange(string literal, string described)
    {
        // These used to be folded to -1 and answered with the WHOLE file, while the VS Code host
        // refused the call: two hosts, two different answers, one of them far too much text.
        Assert.False(Parse($"[{{\"path\":\"a.cs\",\"startLine\":{literal}}}]", out var files, out var refusal));

        Assert.Empty(files);
        Assert.Equal(
            $"Failed to read 'files': entry 1 of 1: 'startLine' must be an integer or null, received {described}. " +
            RefusedNothingRead,
            refusal);
    }

    [Theory]
    [InlineData("3000000000")]
    [InlineData("-3000000000")]
    public void TryParse_RangeOutsideInt32_IsRefusedWithTheIntegerRangeReason(string literal)
    {
        Assert.False(Parse($"[{{\"path\":\"a.cs\",\"lineCount\":{literal}}}]", out _, out var refusal));

        Assert.StartsWith("Failed to read 'files': entry 1 of 1: 'lineCount' is outside the 32-bit integer range, received the number ", refusal);
        Assert.EndsWith($". {RefusedNothingRead}", refusal);
    }

    [Fact]
    public void TryParse_NumberThatIsNotFinite_IsRefusedAndNamedAsSent()
    {
        // `1e400` is a valid JSON number and an `int` cannot hold it. `System.Text.Json` cannot parse
        // it as a decimal either, so it lands in the "not an integer" branch — but the sentence must
        // echo `1e400`, not a rounded value, or the model cannot recognise its own payload.
        Assert.False(Parse("[{\"path\":\"a.cs\",\"startLine\":1e400}]", out _, out var refusal));

        Assert.Equal(
            "Failed to read 'files': entry 1 of 1: 'startLine' must be an integer or null, " +
            "received the number 1e400. Nothing was read.",
            refusal);
    }

    [Theory]
    [InlineData("118", 118)]
    [InlineData("118.0", 118)]
    [InlineData("1e2", 100)]
    [InlineData("-3.0", -1)] // negative means "no range" and is normalised, not refused
    [InlineData("0", -1)]
    [InlineData("-1", -1)]
    [InlineData("null", -1)]
    public void TryParse_IntegralRange_IsAcceptedAndNormalised(string literal, int expected)
    {
        Assert.True(Parse($"[{{\"path\":\"a.cs\",\"startLine\":{literal}}}]", out var files, out var refusal));

        Assert.Null(refusal);
        Assert.Equal(expected, Assert.Single(files).StartLine);
    }

    [Fact]
    public void TryParse_AbsentRange_IsNoRange()
    {
        Assert.True(Parse("[{\"path\":\"a.cs\"}]", out var files, out var refusal));

        Assert.Null(refusal);
        var entry = Assert.Single(files);
        Assert.Equal("a.cs", entry.Path);
        // The PLAN is normalised, not the contract: an absent bound and every "no range" spelling
        // are one number by the time the host reads it, so the read loop never branches on null.
        Assert.Equal(-1, entry.StartLine);
        Assert.Equal(-1, entry.LineCount);
    }

    // ── normalisation and dedup ───────────────────────────────────────────────────

    [Fact]
    public void TryParse_ThreeSpellingsOfNoRange_CollapseIntoOneEntry()
    {
        // The dedup key is the whole request, so the spellings must be one number BEFORE the key:
        // otherwise the answer carries the same file three times and the model pays for all three.
        Assert.True(Parse(
            "[{\"path\":\"src/File.cs\",\"startLine\":null,\"lineCount\":null}," +
            "{\"path\":\"src/File.cs\",\"startLine\":-1,\"lineCount\":-1}," +
            "{\"path\":\"src/File.cs\",\"startLine\":0,\"lineCount\":0}," +
            "{\"path\":\"src/File.cs\"}]",
            out var files,
            out var refusal));

        Assert.Null(refusal);
        var entry = Assert.Single(files);
        Assert.Equal("src/File.cs", entry.Path);
        Assert.Equal(-1, entry.StartLine);
        Assert.Equal(-1, entry.LineCount);
    }

    [Fact]
    public void TryParse_TwoRangesOfOneFile_StayTwoReads()
    {
        Assert.True(Parse(
            "[{\"path\":\"a.cs\",\"startLine\":1,\"lineCount\":10}," +
            "{\"path\":\"a.cs\",\"startLine\":100,\"lineCount\":10}]",
            out var files,
            out _));

        Assert.Equal(2, files.Count);
        Assert.Equal(1, files[0].StartLine);
        Assert.Equal(100, files[1].StartLine);
    }

    [Fact]
    public void TryParse_KeepsRequestOrder()
    {
        Assert.True(Parse("[{\"path\":\"b.cs\"},{\"path\":\"a.cs\"}]", out var files, out _));

        Assert.Equal(new[] { "b.cs", "a.cs" }, new[] { files[0].Path, files[1].Path });
    }

    // ── parity with the approval card and with the other host ────────────────────

    [Theory]
    [InlineData("Path", "StartLine", "LineCount")]
    [InlineData("PATH", "STARTLINE", "LINECOUNT")]
    [InlineData("path", "startline", "linecount")]
    public void TryParse_PropertyNamesAreReadIgnoringCase(string path, string startLine, string lineCount)
    {
        // `System.Text.Json` bound these with `PropertyNameCaseInsensitive = true` before this parser
        // existed, and the approval card reads them with `OrdinalIgnoreCase`. A case-sensitive read
        // would refuse a payload the UI had just rendered as a valid call.
        Assert.True(Parse(
            $"[{{\"{path}\":\"a.cs\",\"{startLine}\":2,\"{lineCount}\":3}}]",
            out var files,
            out var refusal));

        Assert.Null(refusal);
        var entry = Assert.Single(files);
        Assert.Equal("a.cs", entry.Path);
        Assert.Equal(2, entry.StartLine);
        Assert.Equal(3, entry.LineCount);
    }

    [Fact]
    public void TryParse_UnknownExtraField_IsIgnored()
    {
        Assert.True(Parse("[{\"path\":\"a.cs\",\"encoding\":\"utf-8\"}]", out var files, out var refusal));

        Assert.Null(refusal);
        Assert.Equal("a.cs", Assert.Single(files).Path);
    }

    [Theory]
    [InlineData("[{\"path\":\"a.cs\",\"startLine\":\"\"}]")]
    [InlineData("[{\"path\":\"a.cs\",\"lineCount\":1.5}]")]
    [InlineData("[\"a.cs\"]")]
    [InlineData("{}")]
    public void TryParse_Refusal_NeverQuotesAJsonPathOrAPosition(string json)
    {
        // The text this replaces was `The JSON value could not be converted to
        // System.Nullable`1[System.Int32]. Path: $[0].startLine | LineNumber: 5`, where every number
        // described the pretty-printed COPY `JsonUtils.GetObject` had serialised, and the CLR type
        // name described an implementation detail. None of that is the model's payload.
        Assert.False(Parse(json, out _, out var refusal));

        Assert.NotNull(refusal);
        Assert.DoesNotContain("LineNumber", refusal, System.StringComparison.Ordinal);
        Assert.DoesNotContain("BytePositionInLine", refusal, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Path: $", refusal, System.StringComparison.Ordinal);
        Assert.DoesNotContain("could not be converted", refusal, System.StringComparison.Ordinal);
        Assert.DoesNotContain("System.Nullable", refusal, System.StringComparison.Ordinal);
        Assert.DoesNotContain("System.Collections", refusal, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_HugeOffendingValue_IsCutAndMarked()
    {
        var huge = new string('x', 500);

        Assert.False(Parse($"[{{\"path\":\"a.cs\",\"startLine\":\"{huge}\"}}]", out _, out var refusal));

        Assert.NotNull(refusal);
        Assert.DoesNotContain(huge, refusal, System.StringComparison.Ordinal);
        // A cut JSON string keeps its opening AND its closing quote: `the string "xxx…"`.
        Assert.Contains("received the string \"", refusal, System.StringComparison.Ordinal);
        Assert.Contains("…\"", refusal, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_Failure_PublishesNothing()
    {
        // The plan is assembled locally and published only on success, so a caller that ignores the
        // return value cannot read half a payload this method already refused.
        Assert.False(Parse("[{\"path\":\"a.cs\"},{\"path\":\"b.cs\",\"startLine\":\"2\"}]", out var files, out _));

        Assert.Empty(files);
    }
}
