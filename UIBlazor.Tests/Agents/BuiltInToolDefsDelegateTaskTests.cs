namespace UIBlazor.Tests.Agents;

public partial class BuiltInToolDefsTests
{
    [Fact]
    public void MapMethodToTool_DelegateTask_HasCorrectName()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert
        Assert.Equal(BuiltInToolEnum.DelegateTask, result.Function.Name);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_HasDescription()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert
        Assert.NotEmpty(result.Function.Description);
        Assert.Contains("sub-agent", result.Function.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("delegate", result.Function.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_HasAllParameters()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert
        var props = result.Function.Parameters.Properties;
        Assert.Equal(3, props.Count);
        Assert.True(props.ContainsKey("task"));
        Assert.True(props.ContainsKey("systemPrompt"));
        Assert.True(props.ContainsKey("allowedTools"));
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_TaskAndSystemPrompt_AreString()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert
        var props = result.Function.Parameters.Properties;
        Assert.Equal("string", props["task"].Type);
        Assert.Equal("string", props["systemPrompt"].Type);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_AllowedTools_AreArraysOfString()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert
        var props = result.Function.Parameters.Properties;
        Assert.Equal("array", props["allowedTools"].Type);
        // Items should be string type (Items is object, actual type is NativePropertyDefinition)
        Assert.NotNull(props["allowedTools"].Items);
        var allowedItems = Assert.IsType<NativePropertyDefinition>(props["allowedTools"].Items);
        Assert.Equal("string", allowedItems.Type);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_AllParametersInRequired()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert - In Strict Mode all parameters are required
        Assert.Equal(3, result.Function.Parameters.Required.Count);
        Assert.Contains("task", result.Function.Parameters.Required);
        Assert.Contains("systemPrompt", result.Function.Parameters.Required);
        Assert.Contains("allowedTools", result.Function.Parameters.Required);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_ArrayParams_AreArrayType()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert - allowedTools is string[]? (nullable reference array)
        // It doesn't have a default value, so it's treated as required in strict mode
        // The type is "array" (not union with null, since no default value)
        var props = result.Function.Parameters.Properties;
        Assert.Equal("array", props["allowedTools"].Type);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_GeneratesValidJsonSchema()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));
        var json = JsonUtils.SerializeCompact(result);

        // Assert - verify it's valid JSON and has expected structure
        Assert.Contains("\"name\":\"delegate_task\"", json);
        Assert.Contains("\"task\"", json);
        Assert.Contains("\"systemPrompt\"", json);
        Assert.Contains("\"allowedTools\"", json);
        Assert.Contains("\"strict\":true", json);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_TaskParameter_HasCorrectDescription()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert
        var taskProp = result.Function.Parameters.Properties["task"];
        Assert.Equal("Clear task description for the sub-agent", taskProp.Description);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_SystemPromptParameter_HasCorrectDescription()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert
        var systemPromptProp = result.Function.Parameters.Properties["systemPrompt"];
        Assert.Equal("System prompt defining the sub-agent's role and expertise", systemPromptProp.Description);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_AllowedToolsParameter_HasCorrectDescription()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert
        var allowedToolsProp = result.Function.Parameters.Properties["allowedTools"];
        Assert.Equal("List of tool names the sub-agent is allowed to use. If null or empty, all tools are available. Only these tools will be accessible; all others are denied.", allowedToolsProp.Description);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_AllParameterDescriptions_AreNotEmpty()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert - every parameter should have a non-empty description
        foreach (var prop in result.Function.Parameters.Properties)
        {
            Assert.False(string.IsNullOrEmpty(prop.Value.Description),
                $"Parameter '{prop.Key}' should have a non-empty description");
        }
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_HasStrictModeEnabled()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert
        Assert.True(result.Function.Strict);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_HasAdditionalPropertiesFalse()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert
        Assert.False(result.Function.Parameters.AdditionalProperties);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_JsonSchema_HasStrictAndAdditionalProperties()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));
        var json = JsonUtils.SerializeCompact(result);

        // Assert - both strict and additionalProperties should be in the JSON
        Assert.Contains("\"strict\":true", json);
        Assert.Contains("\"additionalProperties\":false", json);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_JsonSchema_HasCorrectStructure()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));
        var json = JsonUtils.SerializeCompact(result);

        // Assert - verify the full JSON structure is correct
        // Should have type=function, function with name, description, strict, parameters
        Assert.StartsWith("{\"type\":\"function\",\"function\":{\"name\":\"delegate_task\"", json);
        Assert.Contains("\"parameters\":{\"type\":\"object\"", json);
        Assert.Contains("\"required\":[\"task\",\"systemPrompt\",\"allowedTools\"]", json);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_Extended_ParameterDescriptions_ContainExpectedKeywords()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert - each parameter description contains expected keywords
        var props = result.Function.Parameters.Properties;

        // task: contains "task description" or "Clear task"
        var taskDesc = props["task"].Description!;
        Assert.True(
            taskDesc.Contains("task description", StringComparison.OrdinalIgnoreCase) ||
            taskDesc.Contains("Clear task", StringComparison.OrdinalIgnoreCase),
            $"task description should contain 'task description' or 'Clear task', got: {taskDesc}");

        // systemPrompt: contains "System prompt" or "role"
        var systemPromptDesc = props["systemPrompt"].Description!;
        Assert.True(
            systemPromptDesc.Contains("System prompt", StringComparison.OrdinalIgnoreCase) ||
            systemPromptDesc.Contains("role", StringComparison.OrdinalIgnoreCase),
            $"systemPrompt description should contain 'System prompt' or 'role', got: {systemPromptDesc}");

        // allowedTools: contains "allowed" or "available"
        var allowedToolsDesc = props["allowedTools"].Description!;
        Assert.True(
            allowedToolsDesc.Contains("allowed", StringComparison.OrdinalIgnoreCase) ||
            allowedToolsDesc.Contains("available", StringComparison.OrdinalIgnoreCase),
            $"allowedTools description should contain 'allowed' or 'available', got: {allowedToolsDesc}");
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_Extended_StrictModeIsTrue()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert
        Assert.True(result.Function.Strict);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_Extended_AdditionalPropertiesIsFalse()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert
        Assert.False(result.Function.Parameters.AdditionalProperties);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_Extended_ParameterTypeIsObject()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));

        // Assert
        Assert.Equal("object", result.Function.Parameters.Type);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_Extended_ItemsAreNativePropertyDefinition()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));
        var props = result.Function.Parameters.Properties;

        // Assert - allowedTools items are NativePropertyDefinition with type string
        Assert.NotNull(props["allowedTools"].Items);
        var allowedItems = Assert.IsType<NativePropertyDefinition>(props["allowedTools"].Items);
        Assert.Equal("string", allowedItems.Type);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_Extended_FullJsonSchemaRoundtrip()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));
        var json = JsonUtils.SerializeCompact(result);
        var roundtripped = JsonUtils.Deserialize<NativeToolDefinition>(json);

        // Assert - key fields survive the JSON roundtrip
        Assert.NotNull(roundtripped);
        Assert.Equal("function", roundtripped!.Type);
        Assert.NotNull(roundtripped.Function);
        Assert.Equal(BuiltInToolEnum.DelegateTask, roundtripped.Function.Name);
        Assert.NotEmpty(roundtripped.Function.Description);
        Assert.True(roundtripped.Function.Strict);
        Assert.Equal("object", roundtripped.Function.Parameters.Type);
        Assert.False(roundtripped.Function.Parameters.AdditionalProperties);

        // Properties survive roundtrip
        Assert.Equal(3, roundtripped.Function.Parameters.Properties.Count);
        Assert.True(roundtripped.Function.Parameters.Properties.ContainsKey("task"));
        Assert.True(roundtripped.Function.Parameters.Properties.ContainsKey("systemPrompt"));
        Assert.True(roundtripped.Function.Parameters.Properties.ContainsKey("allowedTools"));

        // Required survives roundtrip
        Assert.Equal(3, roundtripped.Function.Parameters.Required.Count);
        Assert.Contains("task", roundtripped.Function.Parameters.Required);
        Assert.Contains("systemPrompt", roundtripped.Function.Parameters.Required);
        Assert.Contains("allowedTools", roundtripped.Function.Parameters.Required);

        // Parameter types survive roundtrip
        Assert.Equal("string", roundtripped.Function.Parameters.Properties["task"].Type);
        Assert.Equal("string", roundtripped.Function.Parameters.Properties["systemPrompt"].Type);
        Assert.Equal("array", roundtripped.Function.Parameters.Properties["allowedTools"].Type);
    }

    [Fact]
    public void MapMethodToTool_DelegateTask_Extended_DescriptionContainsKeyPhrases()
    {
        // Act
        var result = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask));
        var desc = result.Function.Description;

        // Assert - the function description mentions key concepts
        Assert.Contains("sub-agent", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("delegate", desc, StringComparison.OrdinalIgnoreCase);
        // "cannot delegate further" or "no recursion"
        Assert.True(
            desc.Contains("cannot delegate further", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("no recursion", StringComparison.OrdinalIgnoreCase),
            $"Description should mention 'cannot delegate further' or 'no recursion', got: {desc}");
        Assert.Contains("system prompt", desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tools", desc, StringComparison.OrdinalIgnoreCase);
    }
}
