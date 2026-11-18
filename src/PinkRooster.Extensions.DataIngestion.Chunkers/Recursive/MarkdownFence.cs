namespace PinkRooster.Extensions.DataIngestion.Chunkers;

internal readonly record struct MarkdownFence(int Start, int BodyStart, int BodyEnd, int End, char Delimiter, int MinimumRun)
{
    internal static IEnumerable<MarkdownFence> Find(string text, CancellationToken cancellationToken)
    {
        int opening = -1;
        int body = 0;
        char delimiter = '\0';
        int minimumRun = 0;

        int start = 0;
        while (start < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = EndOfLine(text, start);
            (char marker, int length, int tail) = ReadMarker(text.AsSpan(start, end - start));

            if (opening < 0)
            {
                if (length >= 3 && (marker != '`' || !text.AsSpan(start + tail, end - start - tail).Contains('`')))
                {
                    opening = start;
                    body = end;
                    delimiter = marker;
                    minimumRun = length;
                }
            }
            else if (marker == delimiter && length >= minimumRun && IsClosingTail(text.AsSpan(start + tail, end - start - tail)))
            {
                yield return new MarkdownFence(opening, body, start, end, delimiter, minimumRun);
                opening = -1;
            }

            start = end;
        }
    }

    internal static int EndOfLine(string text, int start)
    {
        int newline = text.IndexOf('\n', start);
        return newline < 0 ? text.Length : newline + 1;
    }

    internal bool WouldCloseEarly(ReadOnlySpan<char> body)
    {
        while (!body.IsEmpty)
        {
            int newline = body.IndexOf('\n');
            int length = newline < 0 ? body.Length : newline + 1;
            ReadOnlySpan<char> line = body[..length];
            (char marker, int run, int tail) = ReadMarker(line);
            if (marker == Delimiter && run >= MinimumRun && IsClosingTail(line[tail..]))
            {
                return true;
            }

            body = body[length..];
        }

        return false;
    }

    private static (char Marker, int Length, int Tail) ReadMarker(ReadOnlySpan<char> line)
    {
        int start = 0;
        while (start < line.Length && line[start] == ' ')
        {
            start++;
        }

        if (start > 3 || start == line.Length || line[start] is not ('`' or '~'))
        {
            return default;
        }

        char marker = line[start];
        int tail = start + 1;
        while (tail < line.Length && line[tail] == marker)
        {
            tail++;
        }

        return (marker, tail - start, tail);
    }

    private static bool IsClosingTail(ReadOnlySpan<char> tail)
    {
        foreach (char ch in tail)
        {
            if (ch is not (' ' or '\t' or '\r' or '\n'))
            {
                return false;
            }
        }

        return true;
    }
}
