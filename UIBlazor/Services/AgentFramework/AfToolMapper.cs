using Microsoft.Extensions.AI;

namespace UIBlazor.Services.AgentFramework;

internal static class AfToolMapper
{
    public static IReadOnlyList<AITool> Map(IEnumerable<Tool> tools)
    {
        var result = new List<AITool>();

        foreach (var tool in tools)
        {
            var fn = tool.NativeTool.Function;
            var schema = AfToolSchema.Serialize(fn);

            result.Add(AIFunctionFactory.CreateDeclaration(fn.Name, fn.Description, schema));
        }

        return result;
    }
}

/// <summary>
/// Shared wire-schema serialization for the AF tool mappers
/// (executable AIFunction tools and declaration-only wire tools).
/// </summary>
internal static class AfToolSchema
{
    /// <summary>
    /// The schema is serialized the same way the legacy code does in the wire payload.
    /// NativePropertyDefinition.Items is declared as object - System.Text.Json serializes
    /// the runtime type, which preserves the exact wire format.
    /// </summary>
    public static JsonElement Serialize(NativeToolFunction fn) => JsonSerializer.SerializeToElement(fn.Parameters);
}
