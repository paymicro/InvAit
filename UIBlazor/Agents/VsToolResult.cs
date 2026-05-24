namespace UIBlazor.Agents;

public record VsToolResult
{
    /// <summary>
    /// Result showed to AI.
    /// </summary>
    public string Result { get; init; } = string.Empty;

    /// <summary>
    /// Indicates if the operation was successful
    /// </summary>
    public bool Success { get; init; } = true;

    /// <summary>
    /// Error message if operation failed
    /// </summary>
    public string ErrorMessage { get; init; } = string.Empty;

    /// <summary>
    /// Optional. Name of tool.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    public static VsToolResult Failed(string name, string errorMessage) => new()
    {
        Name = name,
        Success = false,
        ErrorMessage = errorMessage
    };

    public static VsToolResult Denied(string name) => new()
    {
        Name = name,
        Success = false,
        ErrorMessage = "Execution was denied by user."
    };

    public static VsToolResult Cancelled(string name) => new()
    {
        Name = name,
        Success = false,
        ErrorMessage = "Operation was cancelled."
    };
}
