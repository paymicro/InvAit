namespace UIBlazor.Agents;

public record VsToolResult
{
    /// <summary>
    /// Result showed to AI.
    /// </summary>
    public string Result { get; init; } = string.Empty;

    /// <summary>
    /// Tool arguments
    /// </summary>
    public string Args { get; set; } = string.Empty;

    /// <summary>
    /// Indicates if the operation was successful
    /// </summary>
    public bool Success { get; init; } = true;

    /// <summary>
    /// Error message if operation failed
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Optional. Name of tool.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}