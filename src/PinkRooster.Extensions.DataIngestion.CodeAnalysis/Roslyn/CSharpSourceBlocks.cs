using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.CodeAnalysis;

internal readonly record struct SourceBlock(string Source, int LineOffset);

internal static class CSharpSourceBlocks
{
    private const string FenceWhitespace = " \t\r\n";

    internal static IEnumerable<SourceBlock> Read(IngestionDocumentSection section, CancellationToken cancellationToken)
    {
        int lineAccumulator = 0;
        foreach (IngestionDocumentElement element in section.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element is IngestionDocumentSection child)
            {
                foreach (SourceBlock block in Read(child, cancellationToken))
                {
                    yield return block;
                }

                continue;
            }

            if (element.GetMarkdown() is not { Length: > 0 } text)
            {
                continue;
            }

            if (element.HasMetadata && element.Metadata.ContainsKey(SourceCodeDocumentReader.SourceMarker))
            {
                yield return new SourceBlock(text, 0);
                continue;
            }

            foreach (SourceBlock block in Fences(text, lineAccumulator, cancellationToken))
            {
                yield return block;
            }

            lineAccumulator += CountLines(text);
        }
    }

    private static IEnumerable<SourceBlock> Fences(string text, int baseLineOffset, CancellationToken cancellationToken)
    {
        int cursor = 0;
        int currentLine = 1;
        int bodyStart = 0;
        int bodyStartLine = 1;
        char openMarker = '\0';
        int openLength = 0;
        bool isCSharp = false;
        while (cursor < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = text.IndexOf('\n', cursor);
            int next = end < 0 ? text.Length : end + 1;
            // CommonMark allows only spaces and tabs around fence markers, as MarkdownFence in the Chunkers package does.
            ReadOnlySpan<char> line = text.AsSpan(cursor, next - cursor).TrimEnd(FenceWhitespace);
            int indentation = line.Length - line.TrimStart(' ').Length;
            line = line[indentation..];
            if (indentation <= 3 && !line.IsEmpty && line[0] is '`' or '~')
            {
                char marker = line[0];
                int length = line.Length - line.TrimStart(marker).Length;
                ReadOnlySpan<char> rest = line[length..].Trim(FenceWhitespace);
                if (openLength == 0 && length >= 3 && (marker != '`' || !rest.Contains('`')))
                {
                    openMarker = marker;
                    openLength = length;
                    isCSharp = IsCSharp(rest);
                    bodyStart = next;
                    bodyStartLine = currentLine + 1;
                }
                else if (openLength > 0 && marker == openMarker && length >= openLength && rest.IsEmpty)
                {
                    if (isCSharp && cursor > bodyStart)
                    {
                        yield return new SourceBlock(text[bodyStart..cursor], baseLineOffset + bodyStartLine - 1);
                    }

                    openLength = 0;
                }
            }

            cursor = next;
            currentLine++;
        }
    }

    private static int CountLines(string text)
    {
        int count = 0;
        int index = -1;
        while ((index = text.IndexOf('\n', index + 1)) >= 0)
        {
            count++;
        }

        if (text.Length > 0 && !text.EndsWith('\n'))
        {
            count++;
        }

        return count;
    }

    private static bool IsCSharp(ReadOnlySpan<char> info)
    {
        int separator = info.IndexOfAny(' ', '\t');
        ReadOnlySpan<char> language = separator < 0 ? info : info[..separator];
        return language.Equals("csharp", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("cs", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("c#", StringComparison.OrdinalIgnoreCase);
    }
}
