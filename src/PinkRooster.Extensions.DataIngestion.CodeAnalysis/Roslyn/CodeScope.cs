using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PinkRooster.Extensions.DataIngestion.CodeAnalysis;

internal readonly record struct CodeScope(string? Namespace, string? Type, string? Usings)
{
    internal CodeScope WithNamespace(string name) => this with
    {
        Namespace = Namespace is null ? name : $"{Namespace}.{name}",
    };

    internal CodeScope WithType(BaseTypeDeclarationSyntax type, bool signature)
    {
        string kind = type is TypeDeclarationSyntax syntax ? syntax.Keyword.ValueText : "enum";
        string name = type.Identifier.ValueText;
        if (signature)
        {
            if (type is TypeDeclarationSyntax declaration)
            {
                name += declaration.TypeParameterList?.ToString() + declaration.ParameterList?.ToString();
            }

            if (type.BaseList is not null)
            {
                name += $" : {string.Join(", ", type.BaseList.Types)}";
            }
        }

        string label = $"{char.ToUpperInvariant(kind[0])}{kind[1..]}: {name}";
        return this with { Type = Type is null ? label : $"{Type} | {label}" };
    }

    internal CodeScope WithUsings(SyntaxList<UsingDirectiveSyntax> directives, bool include)
    {
        if (!include || directives.Count == 0)
        {
            return this;
        }

        string local = string.Join(" ", directives);
        return this with { Usings = Usings is null ? local : $"{Usings} {local}" };
    }

    internal string? Context(string? member = null)
    {
        string?[] labels = [Namespace is null ? null : $"Namespace: {Namespace}", Type, member, Usings];
        return labels.All(label => label is null) ? null : string.Join(" | ", labels.OfType<string>());
    }
}
