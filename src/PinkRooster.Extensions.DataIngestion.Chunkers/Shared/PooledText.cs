using System.Buffers;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

internal sealed class PooledText : IDisposable
{
    private char[]? _buffer;
    internal int Length { get; set; }
    internal ReadOnlySpan<char> Span => _buffer.AsSpan(0, Length);

    internal void Append(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        int required = checked(Length + text.Length);
        if (_buffer is null || required > _buffer.Length)
        {
            char[] replacement = ArrayPool<char>.Shared.Rent(required);
            Span.CopyTo(replacement);
            ReturnBuffer();
            _buffer = replacement;
        }

        text.CopyTo(_buffer.AsSpan(Length));
        Length = required;
    }

    public override string ToString() => new(Span);

    public void Dispose()
    {
        ReturnBuffer();
        _buffer = null;
        Length = 0;
    }

    private void ReturnBuffer()
    {
        if (_buffer is not null)
        {
            ArrayPool<char>.Shared.Return(_buffer, clearArray: true);
        }
    }
}
