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

    private static NativePropertyDefinition MapTypeToProperty(Type type, string description = "", bool isOptional = false)
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
            prop.Items = MapTypeToProperty(elementType ?? typeof(object));
            
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
                var pDesc = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
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
}
