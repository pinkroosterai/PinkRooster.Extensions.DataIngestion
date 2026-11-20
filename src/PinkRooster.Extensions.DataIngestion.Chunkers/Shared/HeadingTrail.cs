using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>
/// The open headings above the current element. Every chunker builds its <c>Context</c> breadcrumb here, so all of
/// them describe the same position with the same text: heading texts without Markdown <c>#</c> markers, joined by
/// <see cref="Separator"/>.
/// </summary>
internal sealed class HeadingTrail
{
    internal const string Separator = " > ";

    private readonly List<Entry> _entries;

    internal HeadingTrail()
        : this([])
    {
    }

    private HeadingTrail(List<Entry> entries) => _entries = entries;

    /// <summary>Gets the breadcrumb of the open headings, or <see langword="null"/> when none is open.</summary>
    internal string? Breadcrumb => _entries.Count == 0 ? null : string.Join(Separator, _entries.Select(entry => entry.Text));

    /// <summary>Gets the innermost open heading, or <see langword="null"/> when none is open.</summary>
    internal string? Leaf => _entries.Count == 0 ? null : _entries[^1].Text;

    /// <summary>Gets the open headings' source Markdown, as chunkers outside this package may report them.</summary>
    internal IReadOnlyList<string> RawHeadings => [.. _entries.Select(entry => entry.Markdown)];

    /// <summary>Returns an independent copy, for a nested section that must not change this trail.</summary>
    internal HeadingTrail Clone() => new([.. _entries]);

    /// <summary>Closes the open headings at or below the header's level, then opens the header.</summary>
    internal void Open(IngestionDocumentHeader header) => Open(header.Level, header.GetMarkdown());

    internal void Open(int? level, string? markdown)
    {
        int depth = level ?? int.MaxValue;
        while (_entries.Count > 0 && _entries[^1].Level >= depth)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }

        string text = Strip(markdown ?? string.Empty);
        if (text.Length > 0)
        {
            _entries.Add(new Entry(depth, markdown!.Trim(), text));
        }
    }

    /// <summary>Removes Markdown heading markers and surrounding whitespace from a heading's text.</summary>
    internal static string Strip(string markdown) => markdown.Trim().TrimStart('#').Trim();

    private readonly record struct Entry(int Level, string Markdown, string Text);
}
