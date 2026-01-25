namespace UIBlazor.Models;

public class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Profile";
    public string Provider { get; set; } = "OpenAI Compatible";
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "---";
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 10_000;
    public bool Stream { get; set; } = true;
    public bool SkipSSL { get; set; } = false;
    public string SystemPrompt { get; set; } = "You are a helpful AI code assistant.";
}
