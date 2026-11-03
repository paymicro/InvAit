namespace UIBlazor.Tests.Agents;

/// <summary>
/// Schema tests for delegate_task
/// array of SubAgentTaskDefinition objects (one per delegated branch).
/// </summary>
public partial class BuiltInToolDefsTests
{
    [Fact]
    public void MapMethodToTool_DelegateTask_HasCorrectName()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        Assert.Equal(BuiltInToolEnum.DelegateTask, result.Function.Name);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_HasDescription()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        Assert.NotEmpty(result.Function.Description);
        Assert.Contains("sub-agent", result.Function.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("delegate", result.Function.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_HasAllParameters()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        var props = result.Function.Parameters.Properties;
        Assert.Equal(1, props.Count);
        Assert.True(props.ContainsKey("tasks"));
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_TasksParameter_IsArrayOfTaskObjects()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        var tasks = result.Function.Parameters.Properties["tasks"];
        Assert.Equal("array", Assert.IsType<string>(tasks.Type));

        // Items describe the SubAgentTaskDefinition object
        var items = Assert.IsType<NativeParameters>(tasks.Items);
        Assert.Equal("object", Assert.IsType<string>(items.Type));
        Assert.Equal(3, items.Properties.Count);
        Assert.True(items.Properties.ContainsKey("task"));
        Assert.True(items.Properties.ContainsKey("systemPrompt"));
        Assert.True(items.Properties.ContainsKey("allowedTools"));
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_TaskAndSystemPrompt_AreString()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        var items = Assert.IsType<NativeParameters>(result.Function.Parameters.Properties["tasks"].Items);
        Assert.Equal("string", Assert.IsType<string>(items.Properties["task"].Type));
        Assert.Equal("string", Assert.IsType<string>(items.Properties["systemPrompt"].Type));
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_AllowedTools_AreArraysOfString()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        var items = Assert.IsType<NativeParameters>(result.Function.Parameters.Properties["tasks"].Items);
        var allowed = items.Properties["allowedTools"];

        // nullable array → union type (array|null) in strict mode
        var type = Assert.IsType<string[]>(allowed.Type);
        Assert.Contains("array", type);
        Assert.Contains("null", type);

        var allowedItems = Assert.IsType<NativePropertyDefinition>(allowed.Items);
        Assert.Equal("string", Assert.IsType<string>(allowedItems.Type));
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_AllParametersInRequired()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // In Strict Mode all TOOL parameters are required; the one param is 'tasks'
        var required = result.Function.Parameters.Required;
        Assert.Equal(["tasks"], required);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_TaskObject_RequiredAreTaskAndSystemPrompt()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        var items = Assert.IsType<NativeParameters>(result.Function.Parameters.Properties["tasks"].Items);
        // allowedTools is nullable (optional) so it must NOT be in the required list
        Assert.Equal(["task", "systemPrompt"], items.Required);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_GeneratesValidJsonSchema()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));
        var json = JsonUtils.SerializeCompact(result);

        Assert.Contains("\"name\":\"delegate_task\"", json);
        Assert.Contains("\"tasks\"", json);
        Assert.Contains("\"strict\":true", json);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_Parameters_HaveCorrectDescriptions()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        var items = Assert.IsType<NativeParameters>(result.Function.Parameters.Properties["tasks"].Items);
        Assert.Equal("Clear task description for the sub-agent", items.Properties["task"].Description);
        Assert.Equal("System prompt defining the sub-agent's role and expertise", items.Properties["systemPrompt"].Description);
        Assert.Equal("List of tool names the sub-agent is allowed to use. If null or empty, all tools are available. Only these tools will be accessible; all others are denied.", items.Properties["allowedTools"].Description);
        Assert.Contains("parallel sub-agent branch", result.Function.Parameters.Properties["tasks"].Description);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_AllParameterDescriptions_AreNotEmpty()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        Assert.False(string.IsNullOrEmpty(result.Function.Parameters.Properties["tasks"].Description));

        var items = Assert.IsType<NativeParameters>(result.Function.Parameters.Properties["tasks"].Items);
        foreach (var prop in items.Properties)
        {
            Assert.False(string.IsNullOrEmpty(prop.Value.Description),
                $"Parameter '{prop.Key}' should have a non-empty description");
        }
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_HasStrictModeEnabled()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        Assert.True(result.Function.Strict);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_HasAdditionalPropertiesFalse()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        Assert.False(result.Function.Parameters.AdditionalProperties);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_JsonSchema_HasStrictAndAdditionalProperties()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));
        var json = JsonUtils.SerializeCompact(result);

        Assert.Contains("\"strict\":true", json);
        Assert.Contains("\"additionalProperties\":false", json);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_JsonSchema_HasCorrectStructure()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));
        var json = JsonUtils.SerializeCompact(result);

        Assert.StartsWith("{\"type\":\"function\",\"function\":{\"name\":\"delegate_task\"", json);
        Assert.Contains("\"parameters\":{\"type\":\"object\"", json);
        Assert.Contains("\"required\":[\"tasks\"]", json);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_Extended_FullJsonSchemaRoundtrip()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));
        var json = JsonUtils.SerializeCompact(result);
        var roundtripped = JsonUtils.Deserialize<NativeToolDefinition>(json);

        Assert.NotNull(roundtripped);
        Assert.Equal("function", roundtripped!.Type);
        Assert.NotNull(roundtripped.Function);
        Assert.Equal(BuiltInToolEnum.DelegateTask, roundtripped.Function.Name);
        Assert.NotEmpty(roundtripped.Function.Description);
        Assert.True(roundtripped.Function.Strict);
        Assert.Equal("object", Assert.IsType<string>(roundtripped.Function.Parameters.Type));
        Assert.False(roundtripped.Function.Parameters.AdditionalProperties);

        var props = roundtripped.Function.Parameters.Properties;
        Assert.Equal(1, props.Count);
        Assert.True(props.ContainsKey("tasks"));

        // Items is exposed as object; after a JSON roundtrip it comes back as JsonElement —
        // validate the task-item schema through the serialized structure instead.
        Assert.Contains("\"required\":[\"task\",\"systemPrompt\"]", json);
        Assert.Contains("\"task\"", json);
        Assert.Contains("\"systemPrompt\"", json);
        Assert.Contains("\"allowedTools\"", json);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_Extended_DescriptionContainsKeyPhrases()
    {
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));
        var desc = result.Function.Description;

        Assert.Contains("sub-agent", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("delegate", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("parallel branch", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("every task has finished", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("system prompt", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tools", desc, StringComparison.OrdinalIgnoreCase);
    }
}
