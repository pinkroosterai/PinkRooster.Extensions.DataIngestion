using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>
/// Reads plain text, Markdown, or markup source streams into structured ingestion documents
/// where elements represent verbatim slices of the input with 1-based start line metadata.
/// </summary>
public sealed class TextDocumentReader : IngestionDocumentReader
{
    private readonly TextDocumentReaderOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="TextDocumentReader"/> class with optional configuration options.
    /// </summary>
    /// <param name="options">Configuration options, or <see langword="null"/> to use defaults.</param>
    public TextDocumentReader(TextDocumentReaderOptions? options = null)
    {
        _options = options ?? new TextDocumentReaderOptions();
    }

    /// <inheritdoc/>
    public override async Task<IngestionDocument> ReadAsync(
        Stream source,
        string identifier,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        using StreamReader reader = new(source, leaveOpen: true);
        string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        IngestionDocument document = new(identifier);
        IngestionDocumentSection section = new();
        document.Sections.Add(section);

        if (string.IsNullOrEmpty(text))
        {
            return document;
        }

        ParseElements(section, text, _options.ExtractMarkdownHeadings, cancellationToken);
        return document;
    }

    private static void ParseElements(
        IngestionDocumentSection section,
        string text,
        bool extractHeadings,
        CancellationToken cancellationToken)
    {
        List<MarkdownFence> fences = [];
        if (extractHeadings)
        {
            foreach (MarkdownFence fence in MarkdownFence.Find(text, cancellationToken))
            {
                fences.Add(fence);
            }
        }

        int fenceIndex = 0;
        int cursor = 0;
        int currentLine = 1;

        int blockStartChar = -1;
        int blockStartLine = -1;
        int blockEndChar = -1;
        int blockEndLine = -1;

        void FlushBlock()
        {
            if (blockStartChar >= 0 && blockEndChar > blockStartChar)
            {
                string blockText = text[blockStartChar..blockEndChar];
                IngestionDocumentParagraph paragraph = new(blockText);
                paragraph.Metadata[ChunkMetadataKeys.StartLine] = blockStartLine;
                paragraph.Metadata[ChunkMetadataKeys.EndLine] = blockEndLine;
                paragraph.Metadata[ChunkMetadataKeys.LineRange] = $"{blockStartLine}-{blockEndLine}";
                section.Elements.Add(paragraph);

                blockStartChar = -1;
                blockStartLine = -1;
                blockEndChar = -1;
                blockEndLine = -1;
            }
        }

        while (cursor < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int next = text.IndexOf('\n', cursor);
            int lineEnd = next < 0 ? text.Length : next + 1;
            ReadOnlySpan<char> lineSpan = text.AsSpan(cursor, lineEnd - cursor);

            while (fenceIndex < fences.Count && fences[fenceIndex].End <= cursor)
            {
                fenceIndex++;
            }

            bool insideFence = fenceIndex < fences.Count &&
                               cursor >= fences[fenceIndex].Start &&
                               cursor < fences[fenceIndex].End;

            if (extractHeadings && !insideFence && TryParseHeading(lineSpan, out int headingLevel, out string headingText))
            {
                FlushBlock();

                IngestionDocumentHeader header = new(headingText)
                {
                    Level = headingLevel,
                };
                header.Metadata[ChunkMetadataKeys.StartLine] = currentLine;
                header.Metadata[ChunkMetadataKeys.EndLine] = currentLine;
                header.Metadata[ChunkMetadataKeys.LineRange] = $"{currentLine}-{currentLine}";
                section.Elements.Add(header);
            }
            else
            {
                bool isBlank = lineSpan.Trim().IsEmpty;
                if (!insideFence && isBlank)
                {
                    FlushBlock();
                }
                else
                {
                    if (blockStartChar < 0)
                    {
                        blockStartChar = cursor;
                        blockStartLine = currentLine;
                    }

                    blockEndChar = lineEnd;
                    blockEndLine = currentLine;
                }
            }

            cursor = lineEnd;
            currentLine++;
        }

        FlushBlock();
    }

    private static bool TryParseHeading(ReadOnlySpan<char> line, out int level, out string headingText)
    {
        level = 0;
        headingText = string.Empty;

        int start = 0;
        while (start < line.Length && line[start] == ' ')
        {
            start++;
        }

        if (start > 3 || start >= line.Length || line[start] != '#')
        {
            return false;
        }

        int hashes = 0;
        while (start + hashes < line.Length && line[start + hashes] == '#')
        {
            hashes++;
        }

        if (hashes < 1 || hashes > 6)
        {
            return false;
        }

        int restIndex = start + hashes;
        if (restIndex < line.Length && line[restIndex] is not (' ' or '\t' or '\r' or '\n'))
        {
            return false;
        }

        level = hashes;
        headingText = line.TrimEnd("\r\n").ToString();
        return true;
    }
}
