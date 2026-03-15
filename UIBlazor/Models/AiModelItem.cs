namespace UIBlazor.Models;

public record AiModelItem(
    string Id,
    string Object,
    int Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy);