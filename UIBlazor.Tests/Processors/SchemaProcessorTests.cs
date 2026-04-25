using System.Text.Json.Nodes;

namespace UIBlazor.Tests.Processors;

public partial class SchemaProcessorTests
{
    // Shared helper methods for all test files
    protected static JsonElement BuildDeepObjectWithType(int depth)
    {
        JsonNode? node = new JsonObject
        {
            ["type"] = "string"
        };
        for (int i = 0; i < depth; i++)
        {
            node = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["prop"] = node
                }
            };
        }
        return JsonSerializer.SerializeToElement(node);
    }
}
