namespace UIBlazor.Models;

public class AiToolToCall
{
    /// <summary>
    /// The name of the function to be executed.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Gets or sets the function's arguments.
    /// </summary>
    public string Arguments { get; set; }
}
