using System.Reflection;
using AwesomeAssertions;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Repository.Tests;

public sealed class PublicApiTests
{
    private const string AssemblyName = "PinkRooster.Extensions.DataIngestion.Chunkers";
    private const BindingFlags PublicMembers = BindingFlags.Public | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void CorePublicApiMatchesSnapshot()
    {
        var lines = new List<string> { "# Public API for " + AssemblyName };
        foreach (var type in Assembly.Load(AssemblyName).GetExportedTypes()
                     .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            lines.Add($"{type.FullName} : {type.BaseType}");
            foreach (var member in type.GetMembers(PublicMembers)
                         .Select(DescribeMember).OfType<string>().Order(StringComparer.Ordinal))
            {
                lines.Add("  " + member);
            }
        }

        string actual = string.Join('\n', lines) + "\n";
        string filename = AssemblyName + ".txt";
        if (Environment.GetEnvironmentVariable("PINKROOSTER_UPDATE_PUBLIC_API") == "1")
        {
            File.WriteAllText(FindSnapshotSource(filename), actual);
            return;
        }

        string snapshot = Path.Combine(AppContext.BaseDirectory, "PublicApi", filename);
        File.ReadAllText(snapshot).ReplaceLineEndings("\n").Should().Be(actual);
    }

    private static string? DescribeMember(MemberInfo member) => member switch
    {
        ConstructorInfo constructor => $"ctor({DescribeParameters(constructor.GetParameters())})",
        MethodInfo { IsSpecialName: false } method =>
            $"method {(method.IsStatic ? "static " : "")}{method.ReturnType} {method.Name}({DescribeParameters(method.GetParameters())})",
        PropertyInfo property => $"property {property.PropertyType} {property.Name} " +
            $"{{{(property.GetMethod?.IsPublic == true ? " get;" : "")}{DescribeSetter(property)} }}",
        FieldInfo { IsLiteral: true } field => $"const {field.FieldType} {field.Name} = {field.GetRawConstantValue()}",
        FieldInfo field => $"field {field.FieldType} {field.Name}",
        EventInfo evt => $"event {evt.EventHandlerType} {evt.Name}",
        _ => null,
    };

    private static string DescribeSetter(PropertyInfo property)
    {
        if (property.SetMethod is not { IsPublic: true } setter)
        {
            return "";
        }

        return setter.ReturnParameter.GetRequiredCustomModifiers()
            .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) ? " init;" : " set;";
    }

    private static string DescribeParameters(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Select(parameter => $"{parameter.ParameterType} {parameter.Name}" +
            (parameter.HasDefaultValue ? $" = {parameter.DefaultValue ?? "null"}" : "")));

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
