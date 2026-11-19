using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace PinkRooster.Extensions.DataIngestion.CodeAnalysis;

internal sealed class DirectiveContext
{
    private readonly string _source;
    private readonly List<Region> _regions = [];
    private readonly List<(int Offset, string[] Symbols)> _conditions = [];

    internal DirectiveContext(CompilationUnitSyntax root, string source, CancellationToken token)
    {
        _source = source;
        Stack<int> openRegions = new Stack<int>();
        List<ConditionalBranch> conditionals = new List<ConditionalBranch>();
        TextLineCollection lines = root.SyntaxTree.GetText(token).Lines;
        foreach (Microsoft.CodeAnalysis.SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            token.ThrowIfCancellationRequested();
            switch (trivia.GetStructure())
            {
                case RegionDirectiveTriviaSyntax region:
                    openRegions.Push(_regions.Count);
                    _regions.Add(new Region(lines.GetLineFromPosition(region.SpanStart).SpanIncludingLineBreak, null));
                    break;
                case EndRegionDirectiveTriviaSyntax end:
                    TextSpan line = lines.GetLineFromPosition(end.SpanStart).SpanIncludingLineBreak;
                    if (openRegions.TryPop(out int index))
                    {
                        Region original = _regions[index];
                        _regions[index] = original with { Partner = line };
                        _regions.Add(new Region(line, original.Span));
                    }
                    else
                    {
                        _regions.Add(new Region(line, null));
                    }

                    break;
                case IfDirectiveTriviaSyntax start:
                    string[] first = Names(start.Condition);
                    conditionals.Add(new ConditionalBranch(first, start.BranchTaken ? first : []));
                    Record(start.FullSpan.End);
                    break;
                case ElifDirectiveTriviaSyntax next when conditionals.Count > 0:
                    string[] previous = conditionals[^1].Tested;
                    string[] tested = [.. previous, .. Names(next.Condition)];
                    conditionals[^1] = new ConditionalBranch(tested, next.BranchTaken ? tested : []);
                    Record(next.FullSpan.End);
                    break;
                case ElseDirectiveTriviaSyntax alternate when conditionals.Count > 0:
                    ConditionalBranch branch = conditionals[^1];
                    conditionals[^1] = branch with { Active = alternate.BranchTaken ? branch.Tested : [] };
                    Record(alternate.FullSpan.End);
                    break;
                case EndIfDirectiveTriviaSyntax finish when conditionals.Count > 0:
                    conditionals.RemoveAt(conditionals.Count - 1);
                    Record(finish.FullSpan.End);
                    break;
            }
        }

        void Record(int offset) => _conditions.Add((offset, conditionals.SelectMany(branch => branch.Active)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
    }

    internal string[] GetSymbols(TextSpan span)
    {
        if (_conditions.Count == 0)
        {
            return [];
        }

        int index = _conditions.FindLastIndex(condition => condition.Offset <= span.Start);
        SortedSet<string> symbols = new SortedSet<string>(StringComparer.Ordinal);
        if (index >= 0)
        {
            symbols.UnionWith(_conditions[index].Symbols);
        }

        for (index++; index < _conditions.Count && _conditions[index].Offset < span.End; index++)
        {
            symbols.UnionWith(_conditions[index].Symbols);
        }

        return symbols.ToArray();
    }

    internal string RemoveUnpairedRegions(TextSpan span)
    {
        StringBuilder? output = null;
        int cursor = span.Start;
        foreach (Region region in _regions)
        {
            if (region.Span.Start >= span.End)
            {
                break;
            }

            if (region.Span.End <= cursor || (span.Contains(region.Span) &&
                region.Partner is { } partner && span.Contains(partner)))
            {
                continue;
            }

            output ??= new StringBuilder();
            int next = Math.Max(cursor, region.Span.Start);
            output.Append(_source.AsSpan(cursor, next - cursor));
            cursor = Math.Min(span.End, region.Span.End);
        }

        if (output is null)
        {
            return _source.Substring(span.Start, span.Length);
        }

        return output.Append(_source.AsSpan(cursor, span.End - cursor)).ToString();
    }

    private static string[] Names(ExpressionSyntax expression) =>
        expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Select(name => name.Identifier.ValueText).ToArray();

    private readonly record struct ConditionalBranch(string[] Tested, string[] Active);
    private readonly record struct Region(TextSpan Span, TextSpan? Partner);
}
