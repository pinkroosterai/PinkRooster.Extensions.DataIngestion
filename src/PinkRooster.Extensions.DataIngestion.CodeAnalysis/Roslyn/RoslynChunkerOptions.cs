using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;

namespace PinkRooster.Extensions.DataIngestion.CodeAnalysis;

/// <summary>Settings for syntax-aware C# chunking.</summary>
/// <param name="tokenizer">The tokenizer used for content and overlap budgets.</param>
public sealed class RoslynChunkerOptions(Tokenizer tokenizer) : IngestionChunkerOptions(tokenizer)
{
    /// <summary>Gets or sets whether the chunk context includes in-scope using directives.</summary>
    public bool IncludeUsingDirectives { get; set; }

    /// <summary>Gets or sets whether enclosing type context includes type parameters, primary constructors and base types.</summary>
    public bool IncludeEnclosingTypeSignature { get; set; } = true;

    /// <summary>Gets or sets whether region directives without their partner in the same chunk are removed.</summary>
    public bool StripRegionTrivia { get; set; } = true;
}
