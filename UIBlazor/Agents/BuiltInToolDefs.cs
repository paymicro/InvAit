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
    [Description("""
        Create a file, or add to an existing one. Encoding and line endings come from .editorconfig;
        an existing file keeps its own encoding and line endings. An existing file is REPLACED unless `mode` says append or prepend.
        """)]
    public static void CreateFile(
        [Description("Relative or absolute file path")] string filePath,
        [Description("Content")] string content,
        [Description("overwrite = REPLACE the whole file, deleting everything already in it — this is what null means. append = add to the end, prepend = add to the start. create and replace are accepted aliases of overwrite. Ignored when the file does not exist yet.")] string? mode = null)
    { }

    [DisplayName(BuiltInToolEnum.EditFile)]
    [Description("""
        Each oldStr must match exactly ONE place: several matches without approximateLine singling one out is a REFUSAL, not a first-match.
        Edits run bottom-to-top (descending approximateLine), not in list order — every oldStr must exist in the file as it is on disk.
        """)]
    public static void EditFile(
        [Description("Relative or absolute path of an EXISTING file")] string filePath,
        [Description("List of pairs 'search/replace'")] DiffEdit[] edits,
        [Description("Apply the edits in memory and report what WOULD change without writing the file")] bool? dryRun = null)
    { }

    [DisplayName(BuiltInToolEnum.SearchFiles)]
    [Description("List files whose RELATIVE PATH matches the regex (file or folder NAME — never file contents). Returns each matching path with its size in KB. To search inside files, use grep.")]
    public static void SearchFiles(
        [Description("Regex pattern, matched against the path")] string regex,
        [Description("Max count of matches. 50 is a good default"), DefaultValue(50)] int maxMatches)
    { }

    [DisplayName(BuiltInToolEnum.Grep)]
    [Description("Grep search within the project. The regex is matched against each LINE individually, so a pattern containing a line break never matches. Returns matching file paths, line numbers and a few surrounding lines.")]
    public static void Grep(
        [Description("Regex pattern")] string regex,
        [Description("Number of context lines printed BEFORE and AFTER each match. 3 is a good default"), DefaultValue(3)] int contextLines,
        [Description("Max count of matches. 50 is a good default"), DefaultValue(50)] int maxMatches)
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
    [Description("Rebuild solution")]
    public static void Build()
    { }

    [DisplayName(BuiltInToolEnum.RunTests)]
    [Description("""
        Rebuilds the solution AND runs all tests.
        Use this immediately to test code or verify changes.
        This tool automatically handles the full rebuild process itself.
        """)]
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
    [Description("""
        To run a shell command (Git Bash). The shell is stateless.
        Do NOT perform actions requiring special/admin privileges.
        Working directory is current project solution. Avoid 'cd /user/project'
        """)]
    public static void Bash(
        [Description("Shell command to execute. Multi line sh script.")] string command,
        [Description("Short one-sentence explanation of WHAT the command does and WHY it is needed, " +
                     "in the user's language. Shown to the user above the command, so it must be readable prose, " +
                     "not a copy of the command.")] string description)
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
    [Description("Switch the current mode. The value must be exactly one of Chat, Agent, Plan (case-insensitive); anything else is refused and the mode is unchanged.")]
    public static void SwitchMode(
        [Description("Mode")] string mode)
    { }

    [DisplayName(BasicEnum.ReadSkillContent)]
    [Description("Load the full content of a skill when you need detailed instructions.")]
    public static void ReadSkillContent(
        [Description("Skill name")] string skillName,
        [Description("Relative path to a file within the skill folder (e.g. 'references/api-spec.md', 'scripts/build.sh'). This argument is REQUIRED and must be a string: pass an EMPTY STRING to get the skill's SKILL.md content and the list of available files.")] string? fileName)
    { }

    [DisplayName(BuiltInToolEnum.DeleteFile)]
    [Description("Delete file")]
    public static void DeleteFile(
        [Description("Relative or absolute filepath")] string path)
    { }

    [DisplayName(BasicEnum.AskUser)]
    [Description("Ask a question and present options for them to choose from. " +
                 "Use this when you need clarification to proceed.")]
    public static void AskUser(
        [Description("The question")] string question,
        [Description("A list of options for the user to choose from. For an open-ended question, provide an empty array. The user can select one of the options or enter their own custom response.")] string[] options)
    { }

    [DisplayName(BuiltInToolEnum.DelegateTask)]
    [Description("Delegate one or more tasks to sub-agents. Each task runs in its own parallel branch with its own system prompt and conversation context, " +
                 "has access to tools, and returns the final result. All tasks run concurrently and the call completes only when every task has finished. " +
                 "Use this for complex subtasks that benefit from focused attention and a specialized prompt; batch independent subtasks into one call to run them in parallel.")]
    public static void DelegateTask(
        [Description("Tasks to delegate. Each task runs in a separate parallel sub-agent branch.")] SubAgentTaskDefinition[] tasks)
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
            var prop = MapTypeToProperty(param.ParameterType, paramDesc, IsOptionalParameter(param));
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
                    // Как и на верхнем уровне: в Strict Mode ВСЕ поля в required, а
                    // опциональность выражается ТОЛЬКО nullable-типом. Раньше union-поля
                    // (replaceAll, approximateLine) выкидывались из required — валидирующий
                    // endpoint отклонял весь запрос, а не один инструмент.
                    Required = [.. arrayObjects.Properties!.Keys]
                }
                : arrayObjects;

            return isOptional ? prop.AsOptionalUnion(NativeToolType.Array) : prop;
        }

        // Обработка объектов (классы, кроме string и примитивов)
        if (type.IsClass && type != typeof(string))
        {
            prop.SetSingleType(NativeToolType.Object);
            prop.Properties = [];

            foreach (var p in type.GetProperties())
            {
                var propName = ToCamelCase(p.Name);

                // Описание и «(default …)» собираются в одном месте: DefaultValueAttribute —
                // единственный источник дефолта вложенного свойства, а optional-ность решает
                // IsOptionalProperty (дефолт ИЛИ nullable-тип).
                var pDesc = WithDefaultNote(
                    p.GetCustomAttribute<DescriptionAttribute>()?.Description,
                    p.GetCustomAttribute<DefaultValueAttribute>()?.Value);
                prop.Properties.Add(propName, MapTypeToProperty(p.PropertyType, pDesc, IsOptionalProperty(p)));
            }

            return isOptional ? prop.AsOptionalUnion(NativeToolType.Object) : prop;
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

            return isOptional ? prop.AsOptionalUnion(NativeToolType.String) : prop;
        }

        // Обработка примитивов
        var baseType = GetBaseType(type);
        prop.SetSingleType(baseType);

        return isOptional ? prop.AsOptionalUnion(baseType) : prop;
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

    /// <summary>
    /// Optionality of a METHOD parameter. Strict mode has no "absent" argument, so this only
    /// decides whether the schema type becomes ["type","null"].
    /// <para>
    /// Deliberately <see cref="ParameterInfo.HasDefaultValue"/> ONLY — nullability is NOT consulted
    /// here. That is why <c>read_skill_content.fileName</c> (<c>string? fileName</c>, no default)
    /// renders as a REQUIRED, non-nullable string while its own description says "If omitted, …".
    /// Treating a nullable reference type as optional is a contract change (it changes what the
    /// model may omit) and is pinned by <c>MapMethodToTool_NullableType_HasUnionTypeWithNull</c>;
    /// the rule lives in this ONE method so that change is a one-line decision later.
    /// </para>
    /// </summary>
    private static bool IsOptionalParameter(ParameterInfo parameter) => parameter.HasDefaultValue;

    /// <summary>
    /// Optionality of a nested OBJECT property: same strict-mode rule as
    /// <see cref="IsOptionalParameter"/>, but C# has no default-value metadata for properties —
    /// <see cref="DefaultValueAttribute"/> is the only way to declare one, so here the attribute
    /// OR a nullable type makes the property optional.
    /// </summary>
    private static bool IsOptionalProperty(PropertyInfo property)
        => property.GetCustomAttribute<DefaultValueAttribute>()?.Value is not null
           || IsNullableProperty(property);

    /// <summary>
    /// Turns a single-typed property into its optional strict-mode form: the union of
    /// <paramref name="types"/> and null. One place to spell it — the four property kinds
    /// (array, object, enum, primitive) used to carry their own copy of
    /// <c>SetUnionTypes(type, NativeToolType.Null)</c>.
    /// </summary>
    private static NativePropertyDefinition AsOptionalUnion(this NativePropertyDefinition prop, params string[] types)
    {
        prop.SetUnionTypes([.. types, NativeToolType.Null]);
        return prop;
    }

    /// <summary>
    /// Appends the «(default 'x')» note the model reads next to a nested property description.
    /// A null default means there is nothing to note and the description comes back untouched.
    /// </summary>
    private static string? WithDefaultNote(string? description, object? defaultValue)
        => defaultValue is null ? description : description + $" (default '{defaultValue}')";

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
