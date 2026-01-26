using UIBlazor.Options;

namespace UIBlazor.Models;

public class ConnectionProfile : BaseOptions
{
    public string Id { get => field; set => SetIfChanged(ref field, value); } = Guid.NewGuid().ToString();
    public string Name { get => field; set => SetIfChanged(ref field, value); } = "New Profile";
    public string Provider { get => field; set => SetIfChanged(ref field, value); } = "OpenAI Compatible";
    public string Endpoint { get => field; set => SetIfChanged(ref field, value.TrimEnd('/', '\\')); } = string.Empty;
    public string Proxy { get => field; set => SetIfChanged(ref field, value); } = string.Empty;
    public string ApiKey { get => field; set => SetIfChanged(ref field, value); } = string.Empty;
    public string ApiKeyHeader { get => field; set => SetIfChanged(ref field, value); } = "Authorization";
    public string Model { get => field; set => SetIfChanged(ref field, value); } = "---";
    public List<string> AvailableModels { get => field; set => SetIfChanged(ref field, value); } = [];
    public double Temperature { get => field; set => SetIfChanged(ref field, value); } = 0.7;
    public int MaxTokens { get => field; set => SetIfChanged(ref field, value); } = 256_000;
    public bool Stream { get => field; set => SetIfChanged(ref field, value); } = true;
    public bool SkipSSL { get => field; set => SetIfChanged(ref field, value); } = false;
    public string SystemPrompt { get => field; set => SetIfChanged(ref field, value); } = "You are a helpful AI code assistant.";
    public int MaxMessages { get => field; set => SetIfChanged(ref field, value); } = 50;
    public int SessionMaxAgeHours { get => field; set => SetIfChanged(ref field, value); } = 24;
    public int MaxRetryAttempts { get => field; set => SetIfChanged(ref field, value); } = 3;
    public int RetryDelaySeconds { get => field; set => SetIfChanged(ref field, value); } = 2;
}
