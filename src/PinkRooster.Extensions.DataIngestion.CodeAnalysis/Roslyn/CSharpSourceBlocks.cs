using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.CodeAnalysis;

internal static class CSharpSourceBlocks
{
    internal static IEnumerable<string> Read(IngestionDocumentSection section, CancellationToken cancellationToken)
    {
        foreach (var element in section.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element is IngestionDocumentSection child)
            {
                foreach (string block in Read(child, cancellationToken))
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
                yield return text;
                continue;
            }

            foreach (string block in Fences(text, cancellationToken))
            {
                yield return block;
            }
        }
    }

    private static IEnumerable<string> Fences(string text, CancellationToken cancellationToken)
    {
        int cursor = 0;
        int bodyStart = 0;
        char openMarker = '\0';
        int openLength = 0;
        bool isCSharp = false;
        while (cursor < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = text.IndexOf('\n', cursor);
            int next = end < 0 ? text.Length : end + 1;
            ReadOnlySpan<char> line = text.AsSpan(cursor, next - cursor).TrimEnd();
            int indentation = line.Length - line.TrimStart(' ').Length;
            line = line[indentation..];
            if (indentation <= 3 && !line.IsEmpty && line[0] is '`' or '~')
            {
                char marker = line[0];
                int length = line.Length - line.TrimStart(marker).Length;
                ReadOnlySpan<char> rest = line[length..].Trim();
                if (openLength == 0 && length >= 3 && (marker != '`' || !rest.Contains('`')))
                {
                    openMarker = marker;
                    openLength = length;
                    isCSharp = IsCSharp(rest);
                    bodyStart = next;
                }
                else if (openLength > 0 && marker == openMarker && length >= openLength && rest.IsEmpty)
                {
                    if (isCSharp && cursor > bodyStart)
                    {
                        yield return text[bodyStart..cursor];
                    }

                    openLength = 0;
                }
            }

            cursor = next;
        }
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
