using System.Collections;
using System.ComponentModel;
using System.Reflection;

namespace UIBlazor.Agents;

public class BuiltInToolDefs
{
    [Description("Request to read the contents of one or more files.")]
    public Task<string> ReadFiles([Description("File information")] ReadFileParams[] filePath)
    {
        // тут чтение файлов
        return Task.FromResult("");
    }

    [Description("Applies a series of Search & Replace edits to the specified file.")]
    public Task<string> ApplyDiff(
        [Description("File path")] string filePath,
        [Description("List of pairs 'search/replace'. Executed sequentially.")] DiffEdit[] edits)
    {
        // тут применение изменений
        return Task.FromResult("1234");
    }

    public static NativeToolDefinition MapMethodToTool(MethodInfo? method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var methodDesc = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

        var tool = new NativeToolDefinition
        {
            Function = new NativeToolFunction
            {
                Name = method.Name,
                Description = methodDesc,
                Parameters = new NativeParameters
                {
                    Type = NativeToolType.Object,
                    Properties = [],
                    Required = []
                }
            }
        };

        var parameters = tool.Function.Parameters;

        foreach (var param in method.GetParameters())
        {
            var paramDesc = param.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
            var paramName = ToCamelCase(param.Name!);

            // В Strict Mode все параметры в required, но для optional - тип с null
            var prop = MapTypeToProperty(param.ParameterType, paramDesc, param.HasDefaultValue);
            parameters.Properties.Add(paramName, prop);
            parameters.Required.Add(paramName);
        }

        return tool;
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
                    Required = [.. arrayObjects.Properties!.Select(p => p.Key)]
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
                var hasDefaultValue = p.GetCustomAttribute<DefaultValueAttribute>() != null;
                var isNullableProperty = IsNullableProperty(p);
                var isPropOptional = hasDefaultValue || isNullableProperty;
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
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    public async Task<string> InvokeToolAsync(string methodName, string argumentsJson)
    {
        // Находим метод по имени (кейс-сенситив или нет - на ваше усмотрение)
        var method = typeof(BuiltInToolDefs).GetMethod(methodName);
        if (method == null)
            return $"Error: Method {methodName} not found.";

        // Настраиваем десериализатор
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            // Converters = { new JsonStringEnumConverter() }
        };

        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];

        // Парсим JSON один раз в документ, чтобы разобрать по параметрам
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;

        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var jsonName = ToCamelCase(p.Name!);

            if (root.TryGetProperty(jsonName, out var propertyElement))
            {
                // Десериализуем конкретный параметр в нужный тип
                args[i] = JsonSerializer.Deserialize(propertyElement.GetRawText(), p.ParameterType, options);
            }
            else if (p.HasDefaultValue)
            {
                args[i] = p.DefaultValue;
            }
            else
            {
                // В Strict Mode это не должно случиться, но для безопасности:
                args[i] = null;
            }
        }

        // Вызываем метод (предполагаем, что они все Task<string>)
        var result = method.Invoke(this, args);

        if (result is Task<string> task)
            return await task;

        return result?.ToString() ?? string.Empty;
    }
}
