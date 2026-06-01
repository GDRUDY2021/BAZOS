namespace BAZOS.Api
{
    public enum TokenType
    {
        Eof, Identifier, Keyword, Number, StringLiteral, Operator,
        LParen, RParen, LBrace, RBrace, LBracket, RBracket,
        Comma, Colon, Semicolon, Dot, Assign, Annotation, Unknown
    }

    public struct Token
    {
        public TokenType Type;
        public string Value;
        public int Line;
    }

    public class Lexer
    {
        private readonly string _source;
        private int _pos = 0;
        private int _line = 1;

        private static readonly HashSet<string> Keywords = new() {
            "int", "void", "string", "return", "fn", "var", "bool", "byte", "float", "double",
            "if", "else", "while", "for", "true", "false", "null",
            "import", "sys", "sys_void",
            "class", "public", "private", "new", "this"
        };

        public Lexer(string source) { _source = source; }

        public Token Next()
        {
            while (_pos < _source.Length)
            {
                char c = _source[_pos];

                if (char.IsWhiteSpace(c))
                {
                    if (c == '\n') _line++;
                    _pos++;
                    continue;
                }

                if (c == '/' && Peek(1) == '/')
                {
                    while (_pos < _source.Length && _source[_pos] != '\n') _pos++;
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    string id = ReadIdentifier();
                    return new Token { Type = Keywords.Contains(id) ? TokenType.Keyword : TokenType.Identifier, Value = id, Line = _line };
                }

                if (char.IsDigit(c)) return new Token { Type = TokenType.Number, Value = ReadNumber(), Line = _line };
                if (c == '"') return new Token { Type = TokenType.StringLiteral, Value = ReadString(), Line = _line };

                if (c == '=') { if (Peek(1) == '=') { _pos += 2; return new Token { Type = TokenType.Operator, Value = "==", Line = _line }; } _pos++; return new Token { Type = TokenType.Assign, Value = "=", Line = _line }; }
                if (c == '+') { if (Peek(1) == '=') { _pos += 2; return new Token { Type = TokenType.Operator, Value = "+=", Line = _line }; } _pos++; return new Token { Type = TokenType.Operator, Value = "+", Line = _line }; }
                if (c == '-') { if (Peek(1) == '=') { _pos += 2; return new Token { Type = TokenType.Operator, Value = "-=", Line = _line }; } _pos++; return new Token { Type = TokenType.Operator, Value = "-", Line = _line }; }
                if (c == '*') { if (Peek(1) == '=') { _pos += 2; return new Token { Type = TokenType.Operator, Value = "*=", Line = _line }; } _pos++; return new Token { Type = TokenType.Operator, Value = "*", Line = _line }; }
                if (c == '/') { if (Peek(1) == '=') { _pos += 2; return new Token { Type = TokenType.Operator, Value = "/=", Line = _line }; } _pos++; return new Token { Type = TokenType.Operator, Value = "/", Line = _line }; }

                if (c == '<') { if (Peek(1) == '=') { _pos += 2; return new Token { Type = TokenType.Operator, Value = "<=", Line = _line }; } _pos++; return new Token { Type = TokenType.Operator, Value = "<", Line = _line }; }
                if (c == '>') { if (Peek(1) == '=') { _pos += 2; return new Token { Type = TokenType.Operator, Value = ">=", Line = _line }; } _pos++; return new Token { Type = TokenType.Operator, Value = ">", Line = _line }; }
                if (c == '!') { if (Peek(1) == '=') { _pos += 2; return new Token { Type = TokenType.Operator, Value = "!=", Line = _line }; } _pos++; return new Token { Type = TokenType.Operator, Value = "!", Line = _line }; }

                if (c == '&') { _pos++; return new Token { Type = TokenType.Operator, Value = "&", Line = _line }; }
                if (c == '|') { _pos++; return new Token { Type = TokenType.Operator, Value = "|", Line = _line }; }

                if (c == '(') { _pos++; return new Token { Type = TokenType.LParen, Value = "(", Line = _line }; }
                if (c == ')') { _pos++; return new Token { Type = TokenType.RParen, Value = ")", Line = _line }; }
                if (c == '{') { _pos++; return new Token { Type = TokenType.LBrace, Value = "{", Line = _line }; }
                if (c == '}') { _pos++; return new Token { Type = TokenType.RBrace, Value = "}", Line = _line }; }
                if (c == '[') { _pos++; return new Token { Type = TokenType.LBracket, Value = "[", Line = _line }; }
                if (c == ']') { _pos++; return new Token { Type = TokenType.RBracket, Value = "]", Line = _line }; }
                if (c == ';') { _pos++; return new Token { Type = TokenType.Semicolon, Value = ";", Line = _line }; }
                if (c == ',') { _pos++; return new Token { Type = TokenType.Comma, Value = ",", Line = _line }; }
                if (c == '.') { _pos++; return new Token { Type = TokenType.Dot, Value = ".", Line = _line }; }

                _pos++;
                return new Token { Type = TokenType.Unknown, Value = c.ToString(), Line = _line };
            }
            return new Token { Type = TokenType.Eof, Value = "", Line = _line };
        }

        private char Peek(int offset) => _pos + offset < _source.Length ? _source[_pos + offset] : '\0';

        private string ReadIdentifier()
        {
            int start = _pos;
            while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_')) _pos++;
            return _source.Substring(start, _pos - start);
        }

        private string ReadNumber()
        {
            int start = _pos;
            while (_pos < _source.Length && (char.IsDigit(_source[_pos]) || _source[_pos] == '.')) _pos++;
            return _source.Substring(start, _pos - start);
        }

        private string ReadString()
        {
            _pos++; // skip "
            int start = _pos;
            while (_pos < _source.Length && _source[_pos] != '"') _pos++;
            string val = _source.Substring(start, _pos - start);
            if (_pos < _source.Length) _pos++; // skip "
            return val;
        }
    }
}