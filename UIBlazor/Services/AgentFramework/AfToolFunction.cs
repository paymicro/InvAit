using Microsoft.Extensions.AI;

namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Invokable AIFunction for the tool-invocation loop. Keeps the wire schema from the
/// native tool definition untouched and delegates execution to ToolCallHandler, so the
/// approval flow (category rules, "ask user", parallel delegate_task) stays identical.
/// </summary>
internal sealed class AfToolFunction : AIFunction
{
    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _schema;
    private readonly Func<string, string, CancellationToken, Task<string>> _execute;

    public AfToolFunction(
        string name,
        string description,
        JsonElement schema,
        Func<string, string, CancellationToken, Task<string>> execute)
    {
        _name = name;
        _description = description;
        _schema = schema;
        _execute = execute;
    }

    public override string Name => _name;

    public override string Description => _description;

    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var argsJson = JsonSerializer.Serialize((IDictionary<string, object?>)arguments);

        return await _execute(Name, argsJson, cancellationToken);
    }
}

/// <summary>
/// Maps resolved tools into AITools for the ChatOptions.
/// With an executor delegate: invokable <see cref="AfToolFunction"/> for the agent loop
/// (executor signature is (name, argsJson) → result string for the model).
/// Without one: declaration-only <see cref="AIFunctionDeclaration"/> for callers that handle
/// tool calls themselves (single-shot adapter). Both keep the wire schema untouched.
/// </summary>
internal static class AfToolMapper
{
    public static IReadOnlyList<AITool> Map(
        IEnumerable<Tool> tools,
        Func<string, string, CancellationToken, Task<string>>? execute = null)
    {
        var result = new List<AITool>();

        foreach (var tool in tools)
        {
            var fn = tool.NativeTool.Function;
            var schema = AfToolSchema.Serialize(fn);

            result.Add(execute is { } executor
                ? new AfToolFunction(fn.Name, fn.Description, schema, executor)
                : AIFunctionFactory.CreateDeclaration(fn.Name, fn.Description, schema));
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
