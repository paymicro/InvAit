using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UIBlazor.Options;

public class AiOptions : INotifyPropertyChanged
{
    public string Endpoint { get => field; set => SetIfChanged(ref field, value); } = "";

    /// <summary>
    /// Use streaming
    /// </summary>
    public bool Stream { get => field; set => SetIfChanged(ref field, value); } = true;

    /// <summary>
    /// Gets or sets the proxy URL for the AI service, if any. If set, this will override the Endpoint.
    /// </summary>
    public string Proxy { get => field; set => SetIfChanged(ref field, value); } = string.Empty;

    /// <summary>
    /// Gets or sets the API key for authentication with the AI service.
    /// </summary>
    public string ApiKey { get => field; set => SetIfChanged(ref field, value); } = string.Empty;

    /// <summary>
    /// Gets or sets the header name for the API key (e.g., 'Authorization' or 'api-key').
    /// </summary>
    public string ApiKeyHeader { get => field; set => SetIfChanged(ref field, value); } = "Authorization";

    /// <summary>
    /// Gets or sets the model name to use for executing chat completions (e.g., 'gpt-3.5-turbo').
    /// </summary>
    public string Model { get => field; set => SetIfChanged(ref field, value); } = string.Empty;

    /// <summary>
    /// Gets or sets the list of model names on current endpoint.
    /// </summary>
    public List<string> AvailableModels { get => field; set => SetIfChanged(ref field, value); } = [];

    /// <summary>
    /// Gets or sets the system prompt for the AI assistant.
    /// </summary>
    public string SystemPrompt { get => field; set => SetIfChanged(ref field, value); } = "You are a helpful AI code assistant.";

    /// <summary>
    /// Gets or sets the temperature for the AI model (0.0 to 2.0). Set to 0.0 for deterministic responses, higher values for more creative outputs.
    /// </summary>
    public double Temperature { get => field; set => SetIfChanged(ref field, value); } = 0.7;

    /// <summary>
    /// Gets or sets the maximum number of tokens to generate in the response.
    /// </summary>
    public int MaxTokens { get => field; set => SetIfChanged(ref field, value); } = 100_000;

    /// <summary>
    /// Gets or sets the maximum number of messages to keep in conversation memory.
    /// </summary>
    public int MaxMessages { get => field; set => SetIfChanged(ref field, value); } = 50;

    /// <summary>
    /// Gets or sets the maximum age in hours for conversation sessions before cleanup.
    /// </summary>
    public int SessionMaxAgeHours { get => field; set => SetIfChanged(ref field, value); } = 24;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void SetIfChanged<T>(ref T storage, T value, [CallerMemberName] string prop = "")
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return;

        storage = value;
        RaisePropertyChanged(prop);
    }

    private void RaisePropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}