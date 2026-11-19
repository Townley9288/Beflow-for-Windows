namespace BBDownForWindows.Core;

internal sealed class ProcessOutputBuffer
{
    internal const int Capacity = 64 * 1024;
    private readonly char[] _buffer = new char[Capacity];
    private int _next;
    private int _count;

    public void Append(string text)
    {
        var value = text.AsSpan(Math.Max(0, text.Length - Capacity));
        var first = Math.Min(value.Length, Capacity - _next);
        value[..first].CopyTo(_buffer.AsSpan(_next));
        value[first..].CopyTo(_buffer);
        _next = (_next + value.Length) % Capacity;
        _count = Math.Min(_count + value.Length, Capacity);
    }

    public override string ToString()
    {
        if (_count < Capacity) return new string(_buffer, 0, _count);
        return string.Concat(_buffer.AsSpan(_next), _buffer.AsSpan(0, _next));
    }
}
