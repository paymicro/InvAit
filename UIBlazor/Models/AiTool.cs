namespace UIBlazor.Models;

public class AiTool
{
    public int Index { get; set; } = 0;

    /// <summary>
    /// Used later to submit the function result back to the AI.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// It will probably always be "function", indicating that the execution of a function is being requested.
    /// </summary>
    public string Type { get; set; } = "function";

    /// <summary>
    /// Gets or sets the function to call, represented by the <see cref="AiToolToCall"/> object.
    /// </summary>
    public AiToolToCall Function { get; set; }
}
