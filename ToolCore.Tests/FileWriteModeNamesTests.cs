using Shared.Contracts;

namespace ToolCore.Tests;

/// <summary>
/// The `create_file` mode alias table. It lives in <c>Shared</c> because TWO consumers resolve it:
/// the host (<see cref="FileWriter.ParseMode"/>) and the UI badge that labels the call. The tests
/// below pin the table itself AND the agreement between the two consumers — a badge that resolves
/// aliases differently from the host is a label about something that did not happen.
/// </summary>
public class FileWriteModeNamesTests
{
    [Theory]
    [InlineData(null, FileWriteModeNames.Overwrite)]
    [InlineData("", FileWriteModeNames.Overwrite)]
    [InlineData("overwrite", FileWriteModeNames.Overwrite)]
    [InlineData("create", FileWriteModeNames.Overwrite)]
    [InlineData("replace", FileWriteModeNames.Overwrite)]
    [InlineData("append", FileWriteModeNames.Append)]
    [InlineData("prepend", FileWriteModeNames.Prepend)]
    [InlineData("APPEND", FileWriteModeNames.Append)]
    [InlineData(" append ", FileWriteModeNames.Append)]
    [InlineData("Prepend", FileWriteModeNames.Prepend)]
    public void TryNormalize_ResolvesEveryAcceptedValue(string? mode, string expected)
    {
        Assert.True(FileWriteModeNames.TryNormalize(mode, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("app")]
    [InlineData("overwrit")]
    public void TryNormalize_RejectsUnknownValues_AndReturnsTheRawValue(string mode)
    {
        // The caller shows the raw value: it is what the model must see to fix its own call.
        Assert.False(FileWriteModeNames.TryNormalize(mode, out var normalized));
        Assert.Equal(mode, normalized);
    }

    [Fact]
    public void TryNormalize_UnknownValue_IsTrimmedInTheEcho()
    {
        Assert.False(FileWriteModeNames.TryNormalize("  sideways  ", out var normalized));
        Assert.Equal("sideways", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("overwrite")]
    [InlineData("create")]
    [InlineData("replace")]
    [InlineData("append")]
    [InlineData("prepend")]
    [InlineData("APPEND")]
    [InlineData(" append ")]
    public void ParseMode_AgreesWithTheSharedTable(string? mode)
    {
        // The invariant that makes the badge trustworthy: whatever the UI resolves, the host
        // resolves to the same thing. Two tables would drift silently.
        Assert.True(FileWriteModeNames.TryNormalize(mode, out var normalized));

        var expected = normalized switch
        {
            FileWriteModeNames.Append => FileWriteMode.Append,
            FileWriteModeNames.Prepend => FileWriteMode.Prepend,
            _ => FileWriteMode.Overwrite
        };

        Assert.Equal(expected, FileWriter.ParseMode(mode));
    }

    [Fact]
    public void ParseMode_AndTheTable_RejectTheSameValues()
    {
        // Refusal must be symmetric too: a value the badge calls "unknown" must be one the host
        // refuses, otherwise the UI warns about a call that would have succeeded.
        const string mode = "sideways";

        Assert.False(FileWriteModeNames.TryNormalize(mode, out _));
        Assert.Throws<ArgumentException>(() => FileWriter.ParseMode(mode));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("a", true)]
    [InlineData("app", true)]
    [InlineData("APP", true)]
    [InlineData("pre", true)]
    [InlineData("overw", true)]
    [InlineData("creat", true)]
    [InlineData(" app", true)]
    [InlineData("append", false)]
    [InlineData(" append ", false)]
    [InlineData("create", false)]
    [InlineData("overwrite", false)]
    [InlineData("sideways", false)]
    public void IsPrefixOfAKnownMode_IsTrueOnlyForAValueThatCanStillBecomeAKnownOne(string raw, bool expected)
    {
        // The badge uses this to tell "still streaming" from "the host refuses this": the arguments
        // are repaired to valid JSON at every chunk, so `"append"` arrives mid-stream as the legal
        // `"app"`. Reporting that as an unknown mode claimed a refusal for a call never broken,
        // while a value like `sideways` can never become a mode and IS the refusal.
        Assert.Equal(expected, FileWriteModeNames.IsPrefixOfAKnownMode(raw));
    }
}
