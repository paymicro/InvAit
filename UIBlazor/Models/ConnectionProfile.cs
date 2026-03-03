namespace UIBlazor.Models;

public class ConnectionProfile : BaseOptions
{
    public string Id { get; init => SetIfChanged(ref field, value); } = Guid.NewGuid().ToString();

    public string Name { get; set => SetIfChanged(ref field, value); } = "New Profile";

    public string Provider { get; set => SetIfChanged(ref field, value); } = "OpenAI Compatible";

    public string Endpoint { get; set => SetIfChanged(ref field, value.TrimEnd('/', '\\')); } = string.Empty;

    public string ApiKey { get; set => SetIfChanged(ref field, value); } = string.Empty;

    public string ApiKeyHeader { get; set => SetIfChanged(ref field, value); } = "Authorization";

    public string Model { get; set => SetIfChanged(ref field, value); } = "---";

    public List<string> AvailableModels { get; set => SetIfChanged(ref field, value); } = [];

    public double Temperature { get; set => SetIfChanged(ref field, value); } = 0.7;

    public int MaxTokens { get; set => SetIfChanged(ref field, value); } = 50_000;

    public bool Stream { get; set => SetIfChanged(ref field, value); } = true;

    public bool SkipSSL { get; set => SetIfChanged(ref field, value); } = false;

    public string SystemPrompt { get; set => SetIfChanged(ref field, value); } = string.Empty;

    public int MaxMessages { get; set => SetIfChanged(ref field, value); } = 50;

    // TODO: нужно реализовать
    public int SessionMaxAgeHours { get; set => SetIfChanged(ref field, value); } = 24;
}
