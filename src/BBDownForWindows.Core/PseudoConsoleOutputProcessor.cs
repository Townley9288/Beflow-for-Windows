using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public sealed partial class PseudoConsoleOutputProcessor(int columns = 160, int rows = 40)
{
    private readonly int _columns = Math.Max(40, columns);
    private readonly int _rows = Math.Max(10, rows);
    private readonly char[][] _screen = Enumerable.Range(0, Math.Max(10, rows))
        .Select(_ => Enumerable.Repeat(' ', Math.Max(40, columns)).ToArray())
        .ToArray();
    private int _column;
    private int _row;
    private int _savedColumn;
    private int _savedRow;
    private string _lastProgressLine = string.Empty;
    private string _pendingEscape = string.Empty;

    public IReadOnlyList<string> Feed(string text)
    {
        if (_pendingEscape.Length > 0)
        {
            text = _pendingEscape + text;
            _pendingEscape = string.Empty;
        }
        List<string> output = [];
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\u001b')
            {
                if (!TryFindEscapeEnd(text, index, out _))
                {
                    _pendingEscape = text[index..];
                    break;
                }
                ConsumeEscape(text, ref index);
                continue;
            }

            switch (character)
            {
                case '\r':
                    _column = 0;
                    break;
                case '\n':
                    EmitCompletedLine(output);
                    MoveRow(1);
                    _column = 0;
                    break;
                case '\b':
                    _column = Math.Max(0, _column - 1);
                    break;
                case '\0':
                case '\a':
                    break;
                default:
                    if (!char.IsControl(character)) Write(character);
                    break;
            }
        }

        EmitProgressSnapshot(output);
        return output;
    }

    public IReadOnlyList<string> Complete()
    {
        _pendingEscape = string.Empty;
        List<string> output = [];
        EmitCompletedLine(output);
        return output;
    }

    private static bool TryFindEscapeEnd(string text, int index, out int end)
    {
        end = -1;
        if (index + 1 >= text.Length) return false;
        var marker = text[index + 1];
        if (marker == ']')
        {
            for (var cursor = index + 2; cursor < text.Length; cursor++)
            {
                if (text[cursor] == '\a')
                {
                    end = cursor;
                    return true;
                }
                if (text[cursor] == '\u001b' && cursor + 1 < text.Length && text[cursor + 1] == '\\')
                {
                    end = cursor + 1;
                    return true;
                }
            }
            return false;
        }
        if (marker == '[')
        {
            for (var cursor = index + 2; cursor < text.Length; cursor++)
            {
                if (text[cursor] is >= '@' and <= '~')
                {
                    end = cursor;
                    return true;
                }
            }
            return false;
        }
        end = index + 1;
        return true;
    }

    private void ConsumeEscape(string text, ref int index)
    {
        if (index + 1 >= text.Length) return;
        var marker = text[index + 1];
        if (marker == ']')
        {
            index += 2;
            while (index < text.Length)
            {
                if (text[index] == '\a') return;
                if (text[index] == '\u001b' && index + 1 < text.Length && text[index + 1] == '\\')
                {
                    index++;
                    return;
                }
                index++;
            }
            return;
        }
        if (marker != '[')
        {
            index++;
            return;
        }

        var start = index + 2;
        var end = start;
        while (end < text.Length && (text[end] < '@' || text[end] > '~')) end++;
        if (end >= text.Length)
        {
            index = text.Length - 1;
            return;
        }

        var parameters = text[start..end];
        ApplyControlSequence(text[end], parameters);
        index = end;
    }

    private void ApplyControlSequence(char command, string parameters)
    {
        var values = ParseParameters(parameters);
        var first = values.Count > 0 ? values[0] : 0;
        switch (command)
        {
            case 'A':
                _row = Math.Max(0, _row - Math.Max(1, first));
                break;
            case 'B':
                _row = Math.Min(_rows - 1, _row + Math.Max(1, first));
                break;
            case 'C':
                _column = Math.Min(_columns - 1, _column + Math.Max(1, first));
                break;
            case 'D':
                _column = Math.Max(0, _column - Math.Max(1, first));
                break;
            case 'G':
                _column = Math.Clamp(Math.Max(1, first) - 1, 0, _columns - 1);
                break;
            case 'H':
            case 'f':
                _row = Math.Clamp((values.Count > 0 && values[0] > 0 ? values[0] : 1) - 1, 0, _rows - 1);
                _column = Math.Clamp((values.Count > 1 && values[1] > 0 ? values[1] : 1) - 1, 0, _columns - 1);
                break;
            case 'J':
                if (first is 0 or 2 or 3) ClearScreen();
                break;
            case 'K':
                EraseLine(first);
                break;
            case 'X':
                EraseCharacters(Math.Max(1, first));
                break;
            case 's':
                _savedColumn = _column;
                _savedRow = _row;
                break;
            case 'u':
                _column = _savedColumn;
                _row = _savedRow;
                break;
        }
    }

    private static List<int> ParseParameters(string parameters)
    {
        var clean = parameters.TrimStart('?', '>', '!');
        if (string.IsNullOrEmpty(clean)) return [];
        return clean.Split(';').Select(item => int.TryParse(item, out var value) ? value : 0).ToList();
    }

    private void Write(char character)
    {
        if (_column >= _columns)
        {
            _column = 0;
            MoveRow(1);
        }
        _screen[_row][_column++] = character;
    }

    private void MoveRow(int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (_row < _rows - 1)
            {
                _row++;
                Array.Fill(_screen[_row], ' ');
                continue;
            }
            for (var row = 1; row < _rows; row++) Array.Copy(_screen[row], _screen[row - 1], _columns);
            Array.Fill(_screen[^1], ' ');
        }
    }

    private void EmitCompletedLine(List<string> output)
    {
        var line = GetLine(_row);
        if (string.IsNullOrWhiteSpace(line) || ProgressRegex().IsMatch(line)) return;
        output.Add(line + Environment.NewLine);
    }

    private void EmitProgressSnapshot(List<string> output)
    {
        var line = GetLine(_row);
        if (!ProgressRegex().IsMatch(line) || string.Equals(line, _lastProgressLine, StringComparison.Ordinal)) return;
        _lastProgressLine = line;
        output.Add(line);
    }

    private string GetLine(int row) => new string(_screen[Math.Clamp(row, 0, _rows - 1)]).TrimEnd();

    private void ClearScreen()
    {
        foreach (var row in _screen) Array.Fill(row, ' ');
        _column = 0;
        _row = 0;
    }

    private void EraseLine(int mode)
    {
        if (mode == 2)
        {
            Array.Fill(_screen[_row], ' ');
            return;
        }
        if (mode == 1)
        {
            for (var column = 0; column <= _column && column < _columns; column++) _screen[_row][column] = ' ';
            return;
        }
        for (var column = _column; column < _columns; column++) _screen[_row][column] = ' ';
    }

    private void EraseCharacters(int count)
    {
        for (var column = _column; column < Math.Min(_columns, _column + count); column++) _screen[_row][column] = ' ';
    }

    [GeneratedRegex("(?<!\\d)\\d{1,3}%")]
    private static partial Regex ProgressRegex();
}
