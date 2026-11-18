using System.Text;
using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

internal sealed class DocumentContext
{
    private readonly string _text;
    private readonly string _normalized;
    private readonly int[] _sourceOffsets;
    private readonly HeadingRange[] _headings;
    private readonly (int Start, int End)[] _windows;

    internal DocumentContext(IngestionDocument document, TokenBudget budget, int overlap, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        List<HeadingRange> headings = [];
        foreach (var section in document.Sections)
        {
            AppendSection(section, [], text, headings, cancellationToken);
        }

        _text = text.ToString();
        List<int> offsets = [];
        _normalized = Normalize(_text, cancellationToken, offsets);
        _sourceOffsets = offsets.ToArray();
        _headings = headings.ToArray();
        _windows = FindWindows(_text, budget, overlap, cancellationToken).ToArray();
    }

    internal ReadOnlyMemory<char>? Find(IngestionChunk<string> chunk, CancellationToken cancellationToken)
    {
        if (_windows.Length == 0)
        {
            return null;
        }

        if (_windows.Length == 1)
        {
            return _text.AsMemory();
        }

        string context = Normalize(chunk.Context ?? "", cancellationToken);
        HeadingRange[] matches = context.Length == 0 ? [] :
            _headings.Where(heading => heading.Breadcrumb == context || heading.PlainBreadcrumb == context).ToArray();
        if (matches.Length == 0 && context.Length > 0)
        {
            matches = _headings.Where(heading => heading.Leaf == context).ToArray();
        }

        int? anchor = FindAnchor(chunk.Content, matches, cancellationToken);
        if (anchor is null && matches.Length > 0)
        {
            anchor = matches[0].Start;
        }

        if (anchor is not int offset)
        {
            return null;
        }

        int low = 0;
        int high = _windows.Length;
        while (low + 1 < high)
        {
            int middle = low + (high - low) / 2;
            if (_windows[middle].Start <= offset)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        var (start, end) = _windows[low];
        return _text.AsMemory(start, end - start);
    }

    private int? FindAnchor(string content, HeadingRange[] candidates, CancellationToken cancellationToken)
    {
        int? Search(string anchor)
        {
            if (anchor.Length == 0)
            {
                return null;
            }

            if (candidates.Length == 0)
            {
                int index = _normalized.IndexOf(anchor, StringComparison.Ordinal);
                return index < 0 ? null : _sourceOffsets[index];
            }

            for (int i = 0; i < candidates.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int start = SourceToNormalized(candidates[i].Start);
                while (i + 1 < candidates.Length && candidates[i].End + 1 == candidates[i + 1].Start)
                {
                    i++;
                }

                int end = SourceToNormalized(candidates[i].End);
                int index = _normalized.IndexOf(anchor, start, end - start, StringComparison.Ordinal);
                if (index >= 0)
                {
                    return _sourceOffsets[index];
                }
            }

            return null;
        }

        if (Search(Normalize(content, cancellationToken)) is int full)
        {
            return full;
        }

        // Some chunkers repeat wrapper lines or omit structural Markdown.
        foreach (string line in content.Split('\n').OrderByDescending(line => line.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string anchor = Normalize(line, cancellationToken);
            if (anchor.Length >= 16 && Search(anchor) is int position)
            {
                return position;
            }
        }

        return null;
    }

    private int SourceToNormalized(int offset)
    {
        int index = Array.BinarySearch(_sourceOffsets, offset);
        return index < 0 ? ~index : index;
    }

    private static IEnumerable<(int Start, int End)> FindWindows(
        string text, TokenBudget budget, int overlap, CancellationToken cancellationToken)
    {
        int start = 0;
        while (start < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = FindWindowLength(text.AsSpan(start), budget, cancellationToken);
            int end = start + length;
            yield return (start, end);
            if (end == text.Length)
            {
                yield break;
            }

            start += overlap == 0
                ? length
                : FindOverlapStart(text.AsSpan(start, length), budget, overlap, cancellationToken);
        }
    }

    private static int FindWindowLength(ReadOnlySpan<char> text, TokenBudget budget, CancellationToken cancellationToken)
    {
        int boundary = Math.Clamp(budget.PrefixIndex(text, budget.Limit), 0, text.Length);
        for (int end = boundary; end > 0; end--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TextBoundaries.IsScalarBoundary(text, end) && budget.Count(text[..end]) <= budget.Limit)
            {
                return end;
            }
        }

        int exact = TextBoundaries.FindPrefix(budget, text, budget.Limit, cancellationToken);
        return exact > 0 ? exact : throw TextBoundaries.NoFit("document context", budget.Limit);
    }

    private static int FindOverlapStart(ReadOnlySpan<char> window, TokenBudget budget, int overlap,
        CancellationToken cancellationToken)
    {
        int start = Math.Clamp(budget.SuffixIndex(window, overlap), 1, window.Length);
        for (; start < window.Length; start++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TextBoundaries.IsScalarBoundary(window, start) && budget.Count(window[start..]) <= overlap)
            {
                break;
            }
        }

        return start;
    }

    private static void AppendSection(IngestionDocumentSection section, List<(int Level, string Text)> breadcrumbs,
        StringBuilder text, List<HeadingRange> headings, CancellationToken cancellationToken)
    {
        foreach (IngestionDocumentElement element in section.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element is IngestionDocumentSection child)
            {
                AppendSection(child, [.. breadcrumbs], text, headings, cancellationToken);
                continue;
            }

            string? markdown = element.GetMarkdown();
            if (string.IsNullOrWhiteSpace(markdown))
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append('\n');
            }

            int start = text.Length;
            text.Append(markdown);
            if (element is IngestionDocumentHeader header)
            {
                int level = header.Level ?? int.MaxValue;
                while (breadcrumbs.Count > 0 && breadcrumbs[^1].Level >= level)
                {
                    breadcrumbs.RemoveAt(breadcrumbs.Count - 1);
                }

                breadcrumbs.Add((level, markdown));
            }

            if (breadcrumbs.Count > 0)
            {
                headings.Add(new HeadingRange(start, text.Length,
                    Normalize(string.Join(" > ", breadcrumbs.Select(item => item.Text)), cancellationToken),
                    Normalize(string.Join(" ", breadcrumbs.Select(item => item.Text)), cancellationToken),
                    Normalize(breadcrumbs[^1].Text, cancellationToken)));
            }
        }
    }

    private static string Normalize(string text, CancellationToken cancellationToken, List<int>? offsets = null)
    {
        var normalized = new StringBuilder(text.Length);
        int whitespace = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if ((i & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            char ch = text[i];
            if (char.IsWhiteSpace(ch))
            {
                if (whitespace < 0)
                {
                    whitespace = i;
                }

                continue;
            }

            if (whitespace >= 0 && normalized.Length > 0)
            {
                normalized.Append(' ');
                offsets?.Add(whitespace);
            }

            whitespace = -1;
            normalized.Append(char.ToUpperInvariant(ch));
            offsets?.Add(i);
        }

        return normalized.ToString();
    }

    private readonly record struct HeadingRange(int Start, int End, string Breadcrumb, string PlainBreadcrumb, string Leaf);
}
