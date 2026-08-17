#pragma warning disable IDE0060 // Remove unused parameter

using System.ComponentModel;
using System.Reflection;

namespace UIBlazor.Agents;

public static class BuiltInToolDefs
{
    [DisplayName(BuiltInToolEnum.ReadFiles)]
    [Description("Read the contents of one or more files")]
    public static void ReadFiles(
        [Description("File information")] ReadFileParams[] files)
    { }

    [DisplayName(BuiltInToolEnum.ReadOpenFile)]
    [Description("Read active file")]
    public static void ReadOpenFile()
    { }

    [DisplayName(BuiltInToolEnum.CreateFile)]
    [Description("Create a new file. The old file will be overwritten if it exists.")]
    public static void CreateFile(
        [Description("Relative or absolute file path")] string filePath,
        [Description("Content")] string content)
    { }

    [DisplayName(BuiltInToolEnum.Edits)]
    [Description("Applies a series of Search & Replace edits to the specified file.")]
    public static void Edit(
        [Description("File path")] string filePath,
        [Description("List of pairs 'search/replace'. Executed sequentially.")] DiffEdit[] edits)
    { }

    [DisplayName(BuiltInToolEnum.SearchFiles)]
    [Description("To return a list of files with patches in solution directory.")]
    public static void SearchFiles(
        [Description("Regex pattern")] string regex,
        [Description("Max count of matches"), DefaultValue(50)] int maxMatches)
    { }

    [DisplayName(BuiltInToolEnum.Grep)]
    [Description("Grep search within the project.")]
    public static void Grep(
        [Description("Regex pattern")] string regex,
        [Description("Lines below and after of match"), DefaultValue(3)] int contextLines,
        [Description("Max count of matches"), DefaultValue(50)] int maxMatches)
    { }

    [DisplayName(BuiltInToolEnum.FindDeclarations)]
    [Description("Find where a type, method, property, or other symbol is DECLARED/DEFINED in C# code.")]
    public static void FindDeclarations(
        [Description("Symbol name")] string symbol)
    { }

    [DisplayName(BuiltInToolEnum.FindReferences)]
    [Description("Find all USAGES (references) of a C# symbol across the solution — where a method is called, a class is used, a property is accessed")]
    public static void FindReferences(
        [Description("Symbol name")] string symbol)
    { }

    [DisplayName(BuiltInToolEnum.Dir)]
    [Description("List files and folders in a given directory")]
    public static void Dir(
        [Description("Directory")] string path,
        [Description("Recursive search"), DefaultValue(false)] bool recursive)
    { }

    [DisplayName(BuiltInToolEnum.Build)]
    [Description("Rebuild solution in Visual Studio")]
    public static void Build()
    { }

    [DisplayName(BuiltInToolEnum.RunTests)]
    [Description("Rebuild and run all tests in solution")]
    public static void RunTests()
    { }

    [DisplayName(BuiltInToolEnum.GetErrors)]
    [Description("Get error list of current solution and current file")]
    public static void GetErrors()
    { }

    [DisplayName(BuiltInToolEnum.GetProjectInfo)]
    [Description("Get information about the solution and projects. Returns list of projects, their types, target frameworks, and file structure.")]
    public static void GetProjectInfo()
    { }

    [DisplayName(BuiltInToolEnum.GetSolutionStructure)]
    [Description("Get a tree-like structure of the entire solution, including projects, folders, and files.")]
    public static void GetSolutionStructure()
    { }

    [DisplayName(BuiltInToolEnum.Bash)]
    [Description("To run a shell command (Git Bash). The shell is stateless. Avoid using single quotes inside your commands if possible. Do NOT perform actions requiring special/admin privileges. Choose terminal commands and scripts optimized for win32 and x64.")]
    public static void Bash(
        [Description("Shell command to execute")] string command)
    { }

    [DisplayName(BuiltInToolEnum.GitStatus)]
    [Description("View git status.")]
    public static void GitStatus()
    { }

    [DisplayName(BuiltInToolEnum.GitLog)]
    [Description("View git commit history with changed files in commits.")]
    public static void GitLog(
        [Description("Number of commits to display")] int number)
    { }

    [DisplayName(BasicEnum.SwitchMode)]
    [Description("Switch the current mode. Available modes: Chat, Agent, Plan.")]
    public static void SwitchMode(
        [Description("Mode")] string mode)
    { }

    [DisplayName(BasicEnum.ReadSkillContent)]
    [Description("Load the full content of a skill when you need detailed instructions.")]
    public static void ReadSkillContent(
        [Description("Skill name")] string skillName)
    { }

    [DisplayName(BuiltInToolEnum.DeleteFile)]
    [Description("Delete file")]
    public static void DeleteFile(
        [Description("Relative or absolute filepath")] string path)
    { }

    [DisplayName(BasicEnum.AskUser)]
    [Description("Ask the user a question and present options for them to choose from. " +
                 "Use this when you need clarification or user input to proceed. " +
                 "The user can select one of the provided options or enter their own answer.")]
    public static void AskUser(
        [Description("The question to ask the user")] string question,
        [Description("A list of options for the user to choose from. Pass an empty array for a free-form question.")] string[] options)
    { }

    [DisplayName(BuiltInToolEnum.DelegateTask)]
    [Description("Delegate a task to a sub-agent. The sub-agent runs with its own system prompt and conversation context, " +
                 "has access to tools, and returns the final result. Sub-agents cannot delegate further (no recursion). " +
                 "Use this for complex subtasks that benefit from focused attention and a specialized prompt.")]
    public static void DelegateTask(
        [Description("Clear task description for the sub-agent")] string task,
        [Description("System prompt defining the sub-agent's role and expertise")] string systemPrompt,
        [Description("List of tool names the sub-agent is allowed to use. If null or empty, all tools are available.")] string[]? allowedTools,
        [Description("List of tool names explicitly denied to the sub-agent")] string[]? deniedTools)
    { }

    public static NativeToolDefinition MapMethodToTool(string methodName)
        => MapMethodToTool(typeof(BuiltInToolDefs).GetMethod(methodName));

    public static NativeToolDefinition MapMethodToTool(MethodInfo? method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var methodDesc = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        var nativeToolDefinition = new NativeToolDefinition
        {
            Function = new NativeToolFunction
            {
                Name = method.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? "undefined",
                Description = methodDesc,
                Parameters = new NativeParameters
                {
                    Type = NativeToolType.Object,
                    Properties = [],
                    Required = []
                }
            }
        };

        var parameters = nativeToolDefinition.Function.Parameters;

        foreach (var param in method.GetParameters())
        {
            if (param.ParameterType == typeof(CancellationToken))
                continue;

            var paramDesc = param.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
            var paramName = ToCamelCase(param.Name!);

            // В Strict Mode все параметры в required, но для optional - тип с null
            var prop = MapTypeToProperty(param.ParameterType, paramDesc, param.HasDefaultValue);
            parameters.Properties.Add(paramName, prop);
            parameters.Required.Add(paramName);
        }

        return nativeToolDefinition;
    }

    private static NativePropertyDefinition MapTypeToProperty(Type type, string? description = null, bool isOptional = false)
    {
        var prop = new NativePropertyDefinition { Description = description };

        // Обработка Nullable<T> и nullable reference types
        var underlyingType = Nullable.GetUnderlyingType(type);
        if (underlyingType != null)
        {
            var innerProp = MapTypeToProperty(underlyingType, description, true);
            return innerProp;
        }

        // Обработка массивов и коллекций
        if (type.IsArray || (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string)))
        {
            prop.SetSingleType(NativeToolType.Array);
            var elementType = type.IsArray
                ? type.GetElementType()
                : type.GetGenericArguments().FirstOrDefault();

            var arrayObjects = MapTypeToProperty(elementType ?? typeof(object));
            prop.Items = string.Equals(arrayObjects.Type.ToString(), NativeToolType.Object, StringComparison.Ordinal)
                ? new NativeParameters
                {
                    Type = NativeToolType.Object,
                    Properties = arrayObjects.Properties!,
                    Required = [.. arrayObjects.Properties!.Where(p => !p.Value.IsUnionType).Select(p => p.Key)]
                }
                : arrayObjects;

            if (isOptional)
            {
                prop.SetUnionTypes(NativeToolType.Array, NativeToolType.Null);
            }
            return prop;
        }

        // Обработка объектов (классы, кроме string и примитивов)
        if (type.IsClass && type != typeof(string))
        {
            prop.SetSingleType(NativeToolType.Object);
            prop.Properties = [];

            foreach (var p in type.GetProperties())
            {
                var pDesc = p.GetCustomAttribute<DescriptionAttribute>()?.Description;
                var propName = ToCamelCase(p.Name);

                // Проверяем, имеет ли свойство дефолтное значение или nullable
                var defaultValue = p.GetCustomAttribute<DefaultValueAttribute>()?.Value;
                if (defaultValue is not null)
                    pDesc += $" (default '{defaultValue}')";
                var isNullableProperty = IsNullableProperty(p);
                var isPropOptional = defaultValue is not null || isNullableProperty;
                prop.Properties.Add(propName, MapTypeToProperty(p.PropertyType, pDesc, isPropOptional));
            }

            if (isOptional)
            {
                prop.SetUnionTypes(NativeToolType.Object, NativeToolType.Null);
            }
            return prop;
        }

        // Обработка enum
        if (type.IsEnum)
        {
            var enumDescription = description;
            if (!string.IsNullOrEmpty(enumDescription))
                enumDescription += " ";
            enumDescription += $"Possible values: {string.Join(", ", Enum.GetNames(type))}";

            prop.Description = enumDescription;
            prop.SetSingleType(NativeToolType.String);

            if (isOptional)
            {
                prop.SetUnionTypes(NativeToolType.String, NativeToolType.Null);
            }
            return prop;
        }

        // Обработка примитивов
        var baseType = GetBaseType(type);
        prop.SetSingleType(baseType);

        if (isOptional)
        {
            prop.SetUnionTypes(baseType, NativeToolType.Null);
        }

        return prop;
    }

    private static string GetBaseType(Type type)
    {
        return type switch
        {
            _ when type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) => NativeToolType.Integer,
            _ when type == typeof(bool) => NativeToolType.Boolean,
            _ when type == typeof(double) || type == typeof(float) || type == typeof(decimal) => NativeToolType.Number,
            _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => NativeToolType.String,
            _ when type == typeof(Guid) => NativeToolType.String,
            _ => NativeToolType.String,
        };
    }

    private static bool IsNullableProperty(PropertyInfo property)
    {
        // Проверка Nullable<T>
        if (Nullable.GetUnderlyingType(property.PropertyType) != null)
            return true;

        // Проверка nullable reference type через аннотации
        var nullabilityContext = new NullabilityInfoContext();
        var nullabilityInfo = nullabilityContext.Create(property);
        return nullabilityInfo.WriteState == NullabilityState.Nullable;
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
