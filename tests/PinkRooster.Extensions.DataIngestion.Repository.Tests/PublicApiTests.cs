using System.Reflection;
using AwesomeAssertions;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Repository.Tests;

public sealed class PublicApiTests
{
    private const BindingFlags PublicMembers = BindingFlags.Public | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Theory]
    [InlineData("PinkRooster.Extensions.DataIngestion.Chunkers")]
    [InlineData("PinkRooster.Extensions.DataIngestion.CodeAnalysis")]
    public void PublicApiMatchesSnapshot(string assemblyName)
    {
        var lines = new List<string> { "# Public API for " + assemblyName };
        foreach (var type in Assembly.Load(assemblyName).GetExportedTypes()
                     .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            lines.Add(DescribeType(type));
            foreach (var member in type.GetMembers(PublicMembers)
                         .Select(DescribeMember).OfType<string>().Order(StringComparer.Ordinal))
            {
                lines.Add("  " + member);
            }
        }

        string actual = string.Join('\n', lines) + "\n";
        string filename = assemblyName + ".txt";
        if (Environment.GetEnvironmentVariable("PINKROOSTER_UPDATE_PUBLIC_API") == "1")
        {
            File.WriteAllText(FindSnapshotSource(filename), actual);
            return;
        }

        string snapshot = Path.Combine(AppContext.BaseDirectory, "PublicApi", filename);
        File.ReadAllText(snapshot).ReplaceLineEndings("\n").Should().Be(actual);
    }

    private static string DescribeType(Type type)
    {
        string kind = type switch
        {
            { IsInterface: true } => "interface",
            { IsEnum: true } => "enum",
            { IsValueType: true } => "struct",
            { IsAbstract: true, IsSealed: true } => "static class",
            { IsAbstract: true } => "abstract class",
            { IsSealed: true } => "sealed class",
            _ => "class",
        };

        IEnumerable<Type> inherited = type.BaseType?.GetInterfaces() ?? [];
        string[] interfaces = [.. type.GetInterfaces().Except(inherited)
            .Select(implemented => implemented.ToString()).Order(StringComparer.Ordinal)];
        string implements = interfaces.Length == 0 ? "" : ", " + string.Join(", ", interfaces);
        return $"{kind} {type.FullName} : {type.BaseType}{implements}";
    }

    private static string? DescribeMember(MemberInfo member)
    {
        var nullability = new NullabilityInfoContext();
        return member switch
        {
            ConstructorInfo constructor => $"ctor({DescribeParameters(constructor.GetParameters(), nullability)})",
            MethodInfo { IsSpecialName: false } method =>
                $"method {(method.IsStatic ? "static " : "")}{DescribeDispatch(method)}" +
                $"{Describe(method.ReturnType, nullability.Create(method.ReturnParameter))} {method.Name}" +
                $"({DescribeParameters(method.GetParameters(), nullability)})",
            PropertyInfo property => $"property {Describe(property.PropertyType, nullability.Create(property))} {property.Name} " +
                $"{{{(property.GetMethod?.IsPublic == true ? " get;" : "")}{DescribeSetter(property)} }}",
            FieldInfo { IsLiteral: true } field => $"const {field.FieldType} {field.Name} = {field.GetRawConstantValue()}",
            FieldInfo field => $"field {Describe(field.FieldType, nullability.Create(field))} {field.Name}",
            EventInfo evt => $"event {Describe(evt.EventHandlerType!, nullability.Create(evt))} {evt.Name}",
            _ => null,
        };
    }

    private static string DescribeDispatch(MethodInfo method) => method switch
    {
        { IsAbstract: true } => "abstract ",
        _ when method.GetBaseDefinition() != method => "override ",
        { IsVirtual: true, IsFinal: false } => "virtual ",
        _ => "",
    };

    private static string Describe(Type type, NullabilityInfo nullability) =>
        !type.IsValueType && nullability.ReadState == NullabilityState.Nullable ? $"{type}?" : type.ToString();

    private static string DescribeSetter(PropertyInfo property)
    {
        if (property.SetMethod is not { IsPublic: true } setter)
        {
            return "";
        }

        return setter.ReturnParameter.GetRequiredCustomModifiers()
            .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) ? " init;" : " set;";
    }

    private static string DescribeParameters(ParameterInfo[] parameters, NullabilityInfoContext nullability) =>
        string.Join(", ", parameters.Select(parameter =>
            $"{Describe(parameter.ParameterType, nullability.Create(parameter))} {parameter.Name}" +
            (parameter.HasDefaultValue ? $" = {DescribeDefault(parameter)}" : "")));

    private static string DescribeDefault(ParameterInfo parameter) => parameter.DefaultValue switch
    {
        null when parameter.ParameterType.IsValueType => "default",
        null => "null",
        string text => $"\"{text}\"",
        var value => value.ToString() ?? "null",
    };

    private static string FindSnapshotSource(string filename)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PinkRooster.Extensions.DataIngestion.slnx")))
            {
                return Path.Combine(directory.FullName, "tests", "PinkRooster.Extensions.DataIngestion.Repository.Tests",
                    "PublicApi", filename);
            }
        }

        throw new InvalidOperationException("Run the API snapshot update from a solution build.");
    }
}
