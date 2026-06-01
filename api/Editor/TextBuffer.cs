using System;
using System.Collections.Generic;

namespace BAZOS.Api.Editor
{
    public sealed class TextBuffer
    {
        private readonly List<string> _lines = new();

        public int Row { get; private set; }
        public int Col { get; private set; }
        public bool IsDirty { get; private set; }

        public int LineCount => _lines.Count;
        public IReadOnlyList<string> Lines => _lines;

        public TextBuffer(string text)
        {
            var normalized = (text ?? string.Empty).Replace("\r", "");
            var lines = normalized.Split('\n');
            if (lines.Length == 0)
                _lines.Add(string.Empty);
            else
                _lines.AddRange(lines);

            if (_lines.Count == 0)
                _lines.Add(string.Empty);
        }

        public string GetText() => string.Join("\n", _lines);

        public void MarkClean() => IsDirty = false;

        public void InsertChar(char c)
        {
            var line = _lines[Row];
            _lines[Row] = line.Insert(Col, c.ToString());
            Col++;
            IsDirty = true;
        }

        public void InsertNewLine()
        {
            var line = _lines[Row];
            var left = line.Substring(0, Col);
            var right = line.Substring(Col);
            _lines[Row] = left;
            _lines.Insert(Row + 1, right);
            Row++;
            Col = 0;
            IsDirty = true;
        }

        public void Backspace()
        {
            if (Col > 0)
            {
                var line = _lines[Row];
                _lines[Row] = line.Remove(Col - 1, 1);
                Col--;
                IsDirty = true;
                return;
            }

            if (Row > 0)
            {
                int prevLen = _lines[Row - 1].Length;
                _lines[Row - 1] += _lines[Row];
                _lines.RemoveAt(Row);
                Row--;
                Col = prevLen;
                IsDirty = true;
            }
        }

        public void Delete()
        {
            var line = _lines[Row];
            if (Col < line.Length)
            {
                _lines[Row] = line.Remove(Col, 1);
                IsDirty = true;
                return;
            }

            if (Row + 1 < _lines.Count)
            {
                _lines[Row] += _lines[Row + 1];
                _lines.RemoveAt(Row + 1);
                IsDirty = true;
            }
        }

        public void MoveLeft()
        {
            if (Col > 0)
                Col--;
            else if (Row > 0)
            {
                Row--;
                Col = _lines[Row].Length;
            }
        }

        public void MoveRight()
        {
            var lineLen = _lines[Row].Length;
            if (Col < lineLen)
                Col++;
            else if (Row + 1 < _lines.Count)
            {
                Row++;
                Col = 0;
            }
        }

        public void MoveUp()
        {
            if (Row <= 0)
                return;
            Row--;
            ClampCol();
        }

        public void MoveDown()
        {
            if (Row + 1 >= _lines.Count)
                return;
            Row++;
            ClampCol();
        }

        public void MoveHome() => Col = 0;
        public void MoveEnd() => Col = _lines[Row].Length;

        public bool GoToLine(int line1Based)
        {
            int idx = line1Based - 1;
            if (idx < 0 || idx >= _lines.Count)
                return false;
            Row = idx;
            ClampCol();
            return true;
        }

        public bool FindNext(string query)
        {
            if (string.IsNullOrEmpty(query))
                return false;

            int startRow = Row;
            int startCol = Col + 1;

            for (int r = startRow; r < _lines.Count; r++)
            {
                int from = r == startRow ? startCol : 0;
                int at = _lines[r].IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    Row = r;
                    Col = at;
                    return true;
                }
            }

            for (int r = 0; r <= startRow; r++)
            {
                int at = _lines[r].IndexOf(query, 0, StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    Row = r;
                    Col = at;
                    return true;
                }
            }

            return false;
        }

        private void ClampCol()
        {
            int len = _lines[Row].Length;
            if (Col > len)
                Col = len;
        }
    }
}

