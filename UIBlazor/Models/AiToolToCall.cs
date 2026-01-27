namespace UIBlazor.Models;

public class AiToolToCall
{
    /// <summary>
    /// The name of the function to be executed.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the function's arguments.
    /// </summary>
    public Dictionary<string, object> Arguments { get; init; } = [];
}
