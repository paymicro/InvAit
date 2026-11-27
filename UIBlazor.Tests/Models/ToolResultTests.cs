namespace UIBlazor.Tests.Models;

/// <summary>
/// Which string a tool result puts on the wire: <c>BuildWire</c> sends
/// <see cref="ToolResult.Content"/> and nothing else, so the selection inside
/// <see cref="ToolResult.Convert"/> is what the model reads.
/// </summary>
public class ToolResultTests
{
    [Fact]
    public void Convert_Success_KeepsPayload()
    {
        var result = ToolResult.Convert(
            new VsToolResult { Success = true, Result = "the answer" }, "Delegate task", "delegate_task");

        Assert.Equal("the answer", result.Content);
        Assert.True(result.Success);
    }

    [Fact]
    public void Convert_FailureWithoutPayload_FallsBackToReason()
    {
        // The shape every refusal without a payload has (VsToolResult.Failed): the reason IS the
        // content, otherwise the model reads an empty tool message.
        var result = ToolResult.Convert(
            VsToolResult.Failed("read_files", "File not found."), "Read files", "read_files");

        Assert.Equal("File not found.", result.Content);
        Assert.False(result.Success);
    }

    [Fact]
    public void Convert_FailureWithPayload_KeepsPayload()
    {
        // The delegate_task shape: a partly failed batch carries the successful branches' answers
        // in Result and the failure count in ErrorMessage. Selecting ErrorMessage on failure
        // dropped every answer and left the model with one sentence.
        var result = ToolResult.Convert(
            new VsToolResult
            {
                Success = false,
                Result = "### Task 1: good\nanswer\n\n### Task 2: bad\n(failed: 404)",
                ErrorMessage = "1 of 2 delegated task(s) failed (Task 2)."
            },
            "Delegate task",
            "delegate_task");

        Assert.Equal("### Task 1: good\nanswer\n\n### Task 2: bad\n(failed: 404)", result.Content);
        Assert.DoesNotContain("failed (Task 2)", result.Content);
        Assert.False(result.Success);
        // The failure is still visible to the user: the badge follows the flag, not the text.
        Assert.StartsWith("❌", result.DisplayName);
    }

    [Fact]
    public void Convert_WhitespaceOnlyPayload_FallsBackToReason()
    {
        // Whitespace is not a payload: it would reach the model as an empty tool message while a
        // real reason sat next to it. Success = true is the ONLY shape where the rule differs from
        // the old one, so it is the one that has to be pinned.
        var result = ToolResult.Convert(
            new VsToolResult { Success = true, Result = "   \n", ErrorMessage = "File not found." },
            "Read files",
            "read_files");

        Assert.Equal("File not found.", result.Content);
    }
}
