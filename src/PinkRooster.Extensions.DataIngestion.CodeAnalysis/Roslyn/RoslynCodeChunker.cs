using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;

namespace PinkRooster.Extensions.DataIngestion.CodeAnalysis;

/// <summary>Chunks raw C# and fenced C# by syntax without compiling the source.</summary>
public sealed class RoslynCodeChunker : IngestionChunker<string>
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp14, DocumentationMode.Parse);
    private readonly Tokenizer _tokenizer;
    private readonly int _maxTokens;
    private readonly int _overlap;
    private readonly bool _includeUsings;
    private readonly bool _includeTypeSignature;
    private readonly bool _stripRegions;

    /// <summary>Creates a chunker with a snapshot of its configuration.</summary>
    /// <param name="options">The syntax and token budget settings.</param>
    public RoslynCodeChunker(RoslynChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Tokenizer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxTokensPerChunk);
        ArgumentOutOfRangeException.ThrowIfNegative(options.OverlapTokens);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(options.OverlapTokens, options.MaxTokensPerChunk);
        _tokenizer = options.Tokenizer;
        _maxTokens = options.MaxTokensPerChunk;
        _overlap = options.OverlapTokens;
        _includeUsings = options.IncludeUsingDirectives;
        _includeTypeSignature = options.IncludeEnclosingTypeSignature;
        _stripRegions = options.StripRegionTrivia;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(IngestionDocument document,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (IngestionDocumentSection section in document.Sections)
        {
            foreach (string source in CSharpSourceBlocks.Read(section, cancellationToken))
            {
                CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(source, ParseOptions, cancellationToken: cancellationToken)
                    .GetCompilationUnitRoot(cancellationToken);
                DirectiveContext directives = new DirectiveContext(root, source, cancellationToken);
                CodeScope scope = new CodeScope(null, null, null).WithUsings(root.Usings, _includeUsings);
                foreach (CodeSlice slice in Partition(source, root.FullSpan, root.Members, scope, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string content = _stripRegions
                        ? directives.RemoveUnpairedRegions(slice.Span)
                        : source.Substring(slice.Span.Start, slice.Span.Length);
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    string[] symbols = directives.GetSymbols(slice.Span);
                    foreach (TextSpan bounded in WithinBudget(content, new TextSpan(0, content.Length), [], 0, cancellationToken))
                    {
                        if (content.AsSpan(bounded.Start, bounded.Length).IsWhiteSpace())
                        {
                            continue;
                        }

                        IngestionChunk<string> chunk = new IngestionChunk<string>(
                            bounded.Length == content.Length ? content : content.Substring(bounded.Start, bounded.Length),
                            document, slice.Context);
                        if (symbols.Length > 0)
                        {
                            chunk.Metadata["conditional_symbols"] = symbols.ToArray();
                        }

                        yield return chunk;
                    }
                }
            }
        }
    }

    private IEnumerable<CodeSlice> Partition(string source, TextSpan span, SyntaxList<MemberDeclarationSyntax> members,
        CodeScope scope, CancellationToken token)
    {
        if (members.Count == 0)
        {
            foreach (CodeSlice slice in Split(source, span, null, scope.Context(), token))
            {
                yield return slice;
            }

            yield break;
        }

        foreach (CodeSlice slice in Split(source, TextSpan.FromBounds(span.Start, members[0].FullSpan.Start),
                     null, scope.Context(), token))
        {
            yield return slice;
        }

        for (int index = 0; index < members.Count;)
        {
            token.ThrowIfCancellationRequested();
            MemberDeclarationSyntax member = members[index++];
            if (member is BaseNamespaceDeclarationSyntax space)
            {
                CodeScope nested = scope.WithNamespace(space.Name.ToString()).WithUsings(space.Usings, _includeUsings);
                foreach (CodeSlice slice in Partition(source, member.FullSpan, space.Members, nested, token))
                {
                    yield return slice;
                }

                continue;
            }

            if (member is BaseTypeDeclarationSyntax type)
            {
                CodeScope nested = scope.WithType(type, _includeTypeSignature);
                bool fits = Fits(source, member.FullSpan);
                IEnumerable<CodeSlice> slices = type is TypeDeclarationSyntax declaration && !fits && declaration.Members.Count > 0
                    ? Partition(source, member.FullSpan, declaration.Members, nested, token)
                    : Split(source, member.FullSpan, member, nested.Context(), token);
                foreach (CodeSlice slice in slices)
                {
                    yield return slice;
                }

                continue;
            }

            TextSpan group = member.FullSpan;
            List<string> names = new List<string> { MemberName(member) };
            if (Fits(source, group))
            {
                while (index < members.Count && members[index] is not (BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax))
                {
                    token.ThrowIfCancellationRequested();
                    MemberDeclarationSyntax next = members[index];
                    TextSpan combined = TextSpan.FromBounds(group.Start, next.FullSpan.End);
                    if (!Fits(source, combined))
                    {
                        break;
                    }

                    names.Add(MemberName(next));
                    group = combined;
                    index++;
                }
            }

            foreach (CodeSlice slice in Split(source, group, member,
                         scope.Context($"Member: {string.Join(", ", names)}"), token))
            {
                yield return slice;
            }
        }

        foreach (CodeSlice slice in Split(source, TextSpan.FromBounds(members[^1].FullSpan.End, span.End),
                     null, scope.Context(), token))
        {
            yield return slice;
        }
    }

    private IEnumerable<CodeSlice> Split(string source, TextSpan span, SyntaxNode? node, string? context,
        CancellationToken token)
    {
        if (span.IsEmpty)
        {
            yield break;
        }

        int[] boundaries = [];
        if (!Fits(source, span) && node is not null)
        {
            boundaries = node.DescendantNodes().Where(child =>
                    child is EnumMemberDeclarationSyntax ||
                    child is StatementSyntax && child.Parent is (BlockSyntax or SwitchSectionSyntax))
                .Select(child =>
                {
                    token.ThrowIfCancellationRequested();
                    return child.FullSpan.Start;
                })
                .Distinct().Order().ToArray();
        }

        foreach (TextSpan part in WithinBudget(source, span, boundaries, _overlap, token))
        {
            yield return new CodeSlice(part, context);
        }
    }

    private IEnumerable<TextSpan> WithinBudget(string source, TextSpan span, int[] boundaries, int overlap,
        CancellationToken token)
    {
        int cursor = span.Start;
        int covered = cursor;
        while (cursor < span.End)
        {
            token.ThrowIfCancellationRequested();
            TextSpan remaining = TextSpan.FromBounds(cursor, span.End);
            if (Fits(source, remaining))
            {
                yield return remaining;
                yield break;
            }

            int limit = FindLimit(source, remaining, token);
            if (limit <= covered)
            {
                if (cursor < covered)
                {
                    cursor = covered;
                    continue;
                }

                throw new InvalidOperationException($"The token budget cannot fit source text at offset {cursor}.");
            }

            int boundaryIndex = Array.BinarySearch(boundaries, limit);
            if (boundaryIndex < 0)
            {
                boundaryIndex = ~boundaryIndex - 1;
            }

            int end = boundaryIndex >= 0 && boundaries[boundaryIndex] > covered
                ? boundaries[boundaryIndex]
                : LastSeparator(source, covered, limit);
            if (!Fits(source, TextSpan.FromBounds(cursor, end)))
            {
                end = limit;
            }

            TextSpan part = TextSpan.FromBounds(cursor, end);
            yield return part;
            covered = end;
            if (end == span.End)
            {
                yield break;
            }

            cursor = overlap == 0 ? end : OverlapStart(source, part, overlap);
        }
    }

    private int FindLimit(string source, TextSpan span, CancellationToken token)
    {
        ReadOnlySpan<char> text = source.AsSpan(span.Start, span.Length);
        int first = text.Length > 1 && char.IsSurrogatePair(text[0], text[1]) ? 2 : 1;
        int length = Math.Max(first, _tokenizer.GetIndexByTokenCount(text, _maxTokens, out _, out _,
            considerNormalization: false));
        while (length >= first)
        {
            token.ThrowIfCancellationRequested();
            if (length < text.Length && char.IsSurrogatePair(text[length - 1], text[length]))
            {
                length--;
            }

            if (length >= first && Fits(source, new TextSpan(span.Start, length)))
            {
                return span.Start + length;
            }

            length--;
        }

        return span.Start;
    }

    private int OverlapStart(string source, TextSpan part, int overlap)
    {
        ReadOnlySpan<char> text = source.AsSpan(part.Start, part.Length);
        int offset = Math.Max(1, _tokenizer.GetIndexByTokenCountFromEnd(text, overlap, out _, out _,
            considerNormalization: false));
        while (offset < text.Length && !char.IsWhiteSpace(text[offset - 1]) && !char.IsWhiteSpace(text[offset]))
        {
            offset++;
        }

        return _tokenizer.CountTokens(text[offset..], considerNormalization: false) <= overlap
            ? part.Start + offset
            : part.End;
    }

    private static int LastSeparator(string source, int covered, int limit)
    {
        int firstContent = limit - source.AsSpan(covered, limit - covered).TrimStart().Length;
        for (int end = limit; end > firstContent; end--)
        {
            if (char.IsWhiteSpace(source[end - 1]))
            {
                return end;
            }
        }

        return limit;
    }

    private bool Fits(string source, TextSpan span) =>
        _tokenizer.CountTokens(source.AsSpan(span.Start, span.Length), considerNormalization: false) <= _maxTokens;

    private static string MemberName(MemberDeclarationSyntax node) => node switch
    {
        BaseMethodDeclarationSyntax method => method switch
        {
            MethodDeclarationSyntax ordinary => ordinary.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            DestructorDeclarationSyntax destructor => $"~{destructor.Identifier.ValueText}",
            OperatorDeclarationSyntax op => $"operator {op.OperatorToken.Text}",
            ConversionOperatorDeclarationSyntax conversion => $"operator {conversion.Type}",
            _ => method.Kind().ToString(),
        },
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        IndexerDeclarationSyntax => "this[]",
        EventDeclarationSyntax evt => evt.Identifier.ValueText,
        BaseFieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText)),
        DelegateDeclarationSyntax declared => declared.Identifier.ValueText,
        GlobalStatementSyntax => "top-level statement",
        _ => node.Kind().ToString(),
    };

    private readonly record struct CodeSlice(TextSpan Span, string? Context);
}
