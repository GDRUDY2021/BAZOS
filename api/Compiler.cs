using System.Text;
using BAZOS.Runtime;

namespace BAZOS.Api
{
    public static class Compiler
    {
        private static Dictionary<string, ushort> _vars = new();
        private static ushort _nextVarId = 0;

        private static Dictionary<string, ushort> _functions = new();
        private static ushort _nextFuncId = 0;

        private static Dictionary<string, byte> _locals = new();
        private static bool _inFunction = false;

        private static byte _tempReg = 0;
        private static List<string> _strings = new();
        private static string _errorMsg;

        private static List<Token> _tokens = new();
        private static int _tokenPos = 0;

        private static Token _current => _tokenPos < _tokens.Count
            ? _tokens[_tokenPos]
            : new Token { Type = TokenType.Eof, Value = "" };

        private static Dictionary<string, byte> _funcArity = new();

        public delegate bool FileReaderDelegate(string path, out string content);
        public static FileReaderDelegate FileReader = null; // Абсолютно чистый делегат

        private static void Advance()
        {
            if (_errorMsg == null && _tokenPos < _tokens.Count)
            {
                _tokenPos++;
            }
        }

        private static bool Match(TokenType type)
        {
            if (_errorMsg != null) return false;

            if (_current.Type == type)
            {
                Advance();
                return true;
            }
            return false;
        }

        private static void Expect(TokenType type)
        {
            if (_errorMsg != null) return;
            if (!Match(type))
            {
                Error($"Expected {type}, got '{_current.Value}' at line {_current.Line}");
            }
        }

        private static void Error(string msg)
        {
            if (_errorMsg == null)
            {
                _errorMsg = msg;
            }
        }

        private static ushort GetFuncId(string name)
        {
            if (!_functions.TryGetValue(name, out var id))
            {
                id = _nextFuncId++;
                _functions[name] = id;
            }
            return id;
        }

        private static ushort GetGlobalId(string name)
        {
            if (!_vars.TryGetValue(name, out var id))
            {
                id = _nextVarId++;
                _vars[name] = id;
            }
            return id;
        }

        private static ushort GetString(string text)
        {
            ushort sIdx = (ushort)_strings.Count;
            _strings.Add(text);
            return sIdx;
        }

        private static bool IsType(string val)
        {
            return val == "int" || val == "string" || val == "float" || val == "bool" || val == "byte";
        }

        public static bool TryCompile(string sourceCode, out byte[] bytecode, out string error)
        {
            bytecode = Array.Empty<byte>();
            error = "";
            _errorMsg = null;
            _vars.Clear();
            _nextVarId = 0;
            _functions.Clear();
            _nextFuncId = 0;
            _locals.Clear();
            _inFunction = false;
            _strings.Clear();
            _funcArity.Clear();

            try
            {
                TokenizeAll(sourceCode);
                if (_errorMsg != null)
                {
                    error = _errorMsg;
                    return false;
                }

                PrePass();

                var code = new List<byte>();
                while (_current.Type != TokenType.Eof && _errorMsg == null)
                {
                    _tempReg = 0;
                    ParseStatement(code);
                }

                if (_errorMsg != null)
                {
                    error = _errorMsg;
                    return false;
                }

                code.Add((byte)VmOpcode.End);
                bytecode = BuildModule(code.ToArray());
                return true;
            }
            catch (Exception ex)
            {
                error = "Fatal Compiler Error: " + ex.Message;
                return false;
            }
        }

        private static void TokenizeAll(string rootCode)
        {
            _tokens.Clear();
            var stack = new Stack<Lexer>();
            var lexer = new Lexer(rootCode);

            while (true)
            {
                var t = lexer.Next();
                if (t.Type == TokenType.Keyword && t.Value == "import")
                {
                    var path = lexer.Next();
                    lexer.Next();

                    if (FS.BazFs.TryReadTextFile(path.Value, out var impCode))
                    {
                        stack.Push(lexer);
                        lexer = new Lexer(impCode);
                    }
                    else
                    {
                        Error($"Import error: File not found '{path.Value}'");
                        return;
                    }
                    continue;
                }

                //WINDOWS
                //if (t.Type == TokenType.Keyword && t.Value == "import")
                //{
                //    var path = lexer.Next();
                //    lexer.Next();

                //    string impCode = "";
                //    if (FileReader != null && FileReader(path.Value, out impCode))
                //    {
                //        stack.Push(lexer);
                //        lexer = new Lexer(impCode);
                //    }
                //    else
                //    {
                //        Error($"Import error: File not found '{path.Value}'");
                //        return;
                //    }
                //    continue;
                //}

                if (t.Type == TokenType.Eof)
                {
                    if (stack.Count > 0)
                    {
                        lexer = stack.Pop();
                        continue;
                    }
                    else
                    {
                        break;
                    }
                }
                _tokens.Add(t);
            }

            _tokens.Add(new Token { Type = TokenType.Eof, Value = "" });
            _tokenPos = 0;
        }

        private static void PrePass()
        {
            for (int i = 0; i < _tokens.Count; i++)
            {
                if (_tokens[i].Value == "fn" && i + 1 < _tokens.Count)
                {
                    string name = _tokens[i + 1].Value;
                    byte args = 0;
                    int j = i + 3;

                    while (j < _tokens.Count && _tokens[j].Type != TokenType.RParen && _tokens[j].Type != TokenType.Eof)
                    {
                        if (_tokens[j].Type == TokenType.Identifier) args++;
                        j++;
                    }
                    _funcArity[name] = args;
                }
                else if (_tokens[i].Value == "class" && i + 1 < _tokens.Count)
                {
                    string cName = _tokens[i + 1].Value;
                    _funcArity[cName + "_ctor"] = 0;
                    int j = i + 2;

                    while (j < _tokens.Count && _tokens[j].Type != TokenType.RBrace && _tokens[j].Type != TokenType.Eof)
                    {
                        if (_tokens[j].Value == "fn" && j + 1 < _tokens.Count)
                        {
                            string mName = _tokens[j + 1].Value;
                            byte args = 1;
                            int k = j + 3;

                            while (k < _tokens.Count && _tokens[k].Type != TokenType.RParen && _tokens[k].Type != TokenType.Eof)
                            {
                                if (_tokens[k].Type == TokenType.Identifier) args++;
                                k++;
                            }
                            _funcArity[cName + "_" + mName] = args;
                        }
                        j++;
                    }
                }
            }
        }

        private static void EmitVarLoad(string name, byte rDst, List<byte> code)
        {
            if (_inFunction && _locals.TryGetValue(name, out byte reg))
            {
                if (rDst != reg)
                {
                    code.Add((byte)VmOpcode.Mov);
                    code.Add(rDst);
                    code.Add(reg);
                }
            }
            else
            {
                ushort vId = GetGlobalId(name);
                code.Add((byte)VmOpcode.GLoad);
                code.Add(rDst);
                code.Add((byte)(vId & 0xFF));
                code.Add((byte)(vId >> 8));
            }
        }

        private static void EmitVarStore(string name, byte rSrc, List<byte> code)
        {
            if (_inFunction && _locals.TryGetValue(name, out byte reg))
            {
                if (rSrc != reg)
                {
                    code.Add((byte)VmOpcode.Mov);
                    code.Add(reg);
                    code.Add(rSrc);
                }
            }
            else
            {
                ushort vId = GetGlobalId(name);
                code.Add((byte)VmOpcode.GStore);
                code.Add((byte)(vId & 0xFF));
                code.Add((byte)(vId >> 8));
                code.Add(rSrc);
            }
        }

        private static void ParseStatement(List<byte> code)
        {
            if (_errorMsg != null) return;

            if (_current.Type == TokenType.Keyword && _current.Value == "class")
            {
                Advance();
                string cName = _current.Value;
                Expect(TokenType.Identifier);
                Expect(TokenType.LBrace);

                var fields = new List<string>();
                var methods = new List<string>();

                while (!Match(TokenType.RBrace) && _current.Type != TokenType.Eof && _errorMsg == null)
                {
                    if (_current.Value == "public" || _current.Value == "private")
                    {
                        Advance();
                        fields.Add(_current.Value);
                        Advance();
                        Expect(TokenType.Semicolon);
                    }
                    else if (_current.Value == "fn")
                    {
                        Advance();
                        string mName = _current.Value;
                        Expect(TokenType.Identifier);
                        methods.Add(mName);

                        ushort mId = GetFuncId(cName + "_" + mName);
                        Expect(TokenType.LParen);

                        bool prevInFunc = _inFunction;
                        var prevLocals = new Dictionary<string, byte>(_locals);
                        byte prevTemp = _tempReg;

                        _inFunction = true;
                        _locals.Clear();
                        _tempReg = 0;

                        _locals["this"] = _tempReg++;

                        while (_current.Type != TokenType.RParen && _current.Type != TokenType.Eof && _errorMsg == null)
                        {
                            if (IsType(_current.Value)) Advance();
                            _locals[_current.Value] = _tempReg++;
                            Advance();

                            if (Match(TokenType.Comma))
                            {
                                if (_current.Type == TokenType.RParen)
                                {
                                    Error("Unexpected comma.");
                                    return;
                                }
                                continue;
                            }
                            break;
                        }
                        Expect(TokenType.RParen);

                        code.Add((byte)VmOpcode.Jmp);
                        int jmpPos = code.Count;
                        code.Add(0);
                        code.Add(0);

                        code.Add((byte)VmOpcode.FuncBegin);
                        code.Add((byte)(mId & 0xFF));
                        code.Add((byte)(mId >> 8));
                        int numRegsPos = code.Count;
                        code.Add(0);

                        Expect(TokenType.LBrace);
                        while (_current.Type != TokenType.RBrace && _current.Type != TokenType.Eof && _errorMsg == null)
                        {
                            ParseStatement(code);
                        }
                        Expect(TokenType.RBrace);

                        code.Add((byte)VmOpcode.RetVoid);
                        code.Add((byte)VmOpcode.FuncEnd);
                        code[numRegsPos] = _tempReg;

                        short skipOff = (short)(code.Count - (jmpPos + 2));
                        code[jmpPos] = (byte)(skipOff & 0xFF);
                        code[jmpPos + 1] = (byte)(skipOff >> 8);

                        _inFunction = prevInFunc;
                        _locals = prevLocals;
                        _tempReg = prevTemp;
                    }
                }

                ushort ctorId = GetFuncId(cName + "_ctor");
                code.Add((byte)VmOpcode.Jmp);
                int jmpCtor = code.Count;
                code.Add(0);
                code.Add(0);

                code.Add((byte)VmOpcode.FuncBegin);
                code.Add((byte)(ctorId & 0xFF));
                code.Add((byte)(ctorId >> 8));
                code.Add(255);

                byte rObj = 0;
                code.Add((byte)VmOpcode.NewStruct);
                code.Add(rObj);
                byte rTemp = 1;

                foreach (var f in fields)
                {
                    code.Add((byte)VmOpcode.LoadNull);
                    code.Add(rTemp);
                    ushort sIdx = GetString(f);

                    code.Add((byte)VmOpcode.SetField);
                    code.Add(rObj);
                    code.Add((byte)(sIdx & 0xFF));
                    code.Add((byte)(sIdx >> 8));
                    code.Add(rTemp);
                }

                foreach (var m in methods)
                {
                    ushort sIdx = GetString(m);
                    ushort fId = GetFuncId(cName + "_" + m);

                    code.Add((byte)VmOpcode.PushFunc);
                    code.Add(rTemp);
                    code.Add((byte)(fId & 0xFF));
                    code.Add((byte)(fId >> 8));

                    code.Add((byte)VmOpcode.SetField);
                    code.Add(rObj);
                    code.Add((byte)(sIdx & 0xFF));
                    code.Add((byte)(sIdx >> 8));
                    code.Add(rTemp);
                }

                code.Add((byte)VmOpcode.Ret);
                code.Add(rObj);
                code.Add((byte)VmOpcode.FuncEnd);

                short skipCtor = (short)(code.Count - (jmpCtor + 2));
                code[jmpCtor] = (byte)(skipCtor & 0xFF);
                code[jmpCtor + 1] = (byte)(skipCtor >> 8);
                return;
            }

            if (_current.Type == TokenType.Keyword && _current.Value == "sys_void")
            {
                Advance();
                Expect(TokenType.LParen);

                if (_current.Type != TokenType.Number)
                {
                    Error("sys_void() requires an integer ID");
                    return;
                }

                ushort sysId = ushort.Parse(_current.Value);
                Advance();

                byte rBase = _tempReg;
                byte argc = 0;

                if (Match(TokenType.Comma))
                {
                    while (_current.Type != TokenType.RParen && _current.Type != TokenType.Eof && _errorMsg == null)
                    {
                        byte argReg = ParseExpression(code);
                        if (argReg != rBase + argc)
                        {
                            code.Add((byte)VmOpcode.Mov);
                            code.Add((byte)(rBase + argc));
                            code.Add(argReg);
                        }
                        argc++;

                        if (Match(TokenType.Comma))
                        {
                            if (_current.Type == TokenType.RParen)
                            {
                                Error("Unexpected ')' after comma");
                                return;
                            }
                            continue;
                        }
                        break;
                    }
                }
                Expect(TokenType.RParen);
                Expect(TokenType.Semicolon);

                _tempReg = (byte)(rBase + argc);
                code.Add((byte)VmOpcode.CallNativeVoid);
                code.Add((byte)(sysId & 0xFF));
                code.Add((byte)(sysId >> 8));
                code.Add(rBase);
                code.Add(argc);
                return;
            }

            if (_current.Type == TokenType.Keyword && _current.Value == "fn")
            {
                Advance();
                string name = _current.Value;
                Expect(TokenType.Identifier);
                ushort fidx = GetFuncId(name);

                Expect(TokenType.LParen);
                bool prevInFunc = _inFunction;
                var prevLocals = new Dictionary<string, byte>(_locals);
                byte prevTemp = _tempReg;

                _inFunction = true;
                _locals.Clear();
                _tempReg = 0;

                while (_current.Type != TokenType.RParen && _current.Type != TokenType.Eof && _errorMsg == null)
                {
                    if (IsType(_current.Value)) Advance();

                    string pName = _current.Value;
                    Expect(TokenType.Identifier);
                    _locals[pName] = _tempReg++;

                    if (Match(TokenType.Comma))
                    {
                        if (_current.Type == TokenType.RParen)
                        {
                            Error("Unexpected comma in function parameters.");
                            return;
                        }
                        continue;
                    }
                    break;
                }
                Expect(TokenType.RParen);

                code.Add((byte)VmOpcode.Jmp);
                int jmpPos = code.Count;
                code.Add(0);
                code.Add(0);

                code.Add((byte)VmOpcode.FuncBegin);
                code.Add((byte)(fidx & 0xFF));
                code.Add((byte)(fidx >> 8));
                int numRegsPos = code.Count;
                code.Add(0);

                Expect(TokenType.LBrace);
                while (_current.Type != TokenType.RBrace && _current.Type != TokenType.Eof && _errorMsg == null)
                {
                    ParseStatement(code);
                }
                Expect(TokenType.RBrace);

                code.Add((byte)VmOpcode.RetVoid);
                code.Add((byte)VmOpcode.FuncEnd);
                code[numRegsPos] = _tempReg;

                short skipOff = (short)(code.Count - (jmpPos + 2));
                code[jmpPos] = (byte)(skipOff & 0xFF);
                code[jmpPos + 1] = (byte)(skipOff >> 8);

                _inFunction = prevInFunc;
                _locals = prevLocals;
                _tempReg = prevTemp;
                return;
            }

            if (_current.Type == TokenType.Keyword && _current.Value == "return")
            {
                Advance();
                if (Match(TokenType.Semicolon))
                {
                    code.Add((byte)VmOpcode.RetVoid);
                }
                else
                {
                    byte rVal = ParseExpression(code);
                    Expect(TokenType.Semicolon);
                    code.Add((byte)VmOpcode.Ret);
                    code.Add(rVal);
                }
                return;
            }

            if (_current.Type == TokenType.Keyword && _current.Value == "if")
            {
                ParseIfStatement(code);
                return;
            }

            if (_current.Type == TokenType.Keyword && _current.Value == "while")
            {
                Advance();
                Expect(TokenType.LParen);
                int condIp = code.Count;
                byte rCond = ParseExpression(code);
                Expect(TokenType.RParen);

                code.Add((byte)VmOpcode.JmpFalse);
                code.Add(rCond);
                int jmpFalsePos = code.Count;
                code.Add(0);
                code.Add(0);

                Expect(TokenType.LBrace);
                while (_current.Type != TokenType.RBrace && _current.Type != TokenType.Eof && _errorMsg == null)
                {
                    ParseStatement(code);
                }
                Expect(TokenType.RBrace);

                code.Add((byte)VmOpcode.Jmp);
                short backOff = (short)(condIp - (code.Count + 2));
                code.Add((byte)(backOff & 0xFF));
                code.Add((byte)(backOff >> 8));

                short skipOff = (short)(code.Count - (jmpFalsePos + 2));
                code[jmpFalsePos] = (byte)(skipOff & 0xFF);
                code[jmpFalsePos + 1] = (byte)(skipOff >> 8);
                return;
            }

            if (_current.Type == TokenType.Keyword && IsType(_current.Value))
            {
                Advance();
                string name = _current.Value;
                Expect(TokenType.Identifier);

                if (_inFunction)
                {
                    byte reg = _tempReg++;
                    _locals[name] = reg;
                    if (Match(TokenType.Assign))
                    {
                        byte rVal = ParseExpression(code);
                        if (rVal != reg)
                        {
                            code.Add((byte)VmOpcode.Mov);
                            code.Add(reg);
                            code.Add(rVal);
                        }
                    }
                    else
                    {
                        code.Add((byte)VmOpcode.LoadNull);
                        code.Add(reg);
                    }
                }
                else
                {
                    ushort vId = GetGlobalId(name);
                    if (Match(TokenType.Assign))
                    {
                        byte rVal = ParseExpression(code);
                        code.Add((byte)VmOpcode.GInit);
                        code.Add((byte)(vId & 0xFF));
                        code.Add((byte)(vId >> 8));
                        code.Add(rVal);
                    }
                }
                Expect(TokenType.Semicolon);
                return;
            }

            if (_current.Type == TokenType.Identifier || (_current.Type == TokenType.Keyword && _current.Value == "this"))
            {
                string name = _current.Value;
                Advance();

                if (_current.Type == TokenType.Assign || (_current.Type == TokenType.Operator && _current.Value.EndsWith("=")))
                {
                    string op = _current.Value;
                    Advance();
                    byte rRight = ParseExpression(code);

                    if (op != "=")
                    {
                        byte rLeft = _tempReg++;
                        EmitVarLoad(name, rLeft, code);
                        byte rRes = _tempReg++;

                        if (op == "+=") code.Add((byte)VmOpcode.Add);
                        else if (op == "-=") code.Add((byte)VmOpcode.Sub);
                        else if (op == "*=") code.Add((byte)VmOpcode.Mul);
                        else if (op == "/=") code.Add((byte)VmOpcode.Div);

                        code.Add(rRes);
                        code.Add(rLeft);
                        code.Add(rRight);
                        rRight = rRes;
                    }
                    EmitVarStore(name, rRight, code);
                    Expect(TokenType.Semicolon);
                    return;
                }
                else if (_current.Type == TokenType.LParen)
                {
                    Advance();
                    byte rBase = _tempReg;
                    byte argc = 0;

                    while (_current.Type != TokenType.RParen && _current.Type != TokenType.Eof && _errorMsg == null)
                    {
                        byte argReg = ParseExpression(code);
                        if (argReg != rBase + argc)
                        {
                            code.Add((byte)VmOpcode.Mov);
                            code.Add((byte)(rBase + argc));
                            code.Add(argReg);
                        }
                        argc++;

                        if (Match(TokenType.Comma))
                        {
                            if (_current.Type == TokenType.RParen)
                            {
                                Error("Unexpected comma");
                                return;
                            }
                            continue;
                        }
                        break;
                    }
                    Expect(TokenType.RParen);
                    Expect(TokenType.Semicolon);

                    if (_funcArity.TryGetValue(name, out byte expected))
                    {
                        if (argc != expected)
                        {
                            Error($"Function '{name}' requires {expected} arguments, but {argc} were given.");
                            return;
                        }
                    }
                    else
                    {
                        Error($"Unknown function '{name}'");
                        return;
                    }

                    ushort fidx = GetFuncId(name);
                    code.Add((byte)VmOpcode.CallVoid);
                    code.Add((byte)(fidx & 0xFF));
                    code.Add((byte)(fidx >> 8));
                    code.Add(rBase);
                    code.Add(argc);
                    return;
                }
                else if (_current.Type == TokenType.LBracket || _current.Type == TokenType.Dot)
                {
                    byte rLeft = _tempReg++;
                    EmitVarLoad(name, rLeft, code);

                    while ((_current.Type == TokenType.LBracket || _current.Type == TokenType.Dot) && _errorMsg == null)
                    {
                        if (_current.Type == TokenType.LBracket)
                        {
                            Advance();
                            byte rIdx = ParseExpression(code);
                            Expect(TokenType.RBracket);

                            if (Match(TokenType.Assign))
                            {
                                byte rVal = ParseExpression(code);
                                Expect(TokenType.Semicolon);
                                code.Add((byte)VmOpcode.ArrStore);
                                code.Add(rLeft);
                                code.Add(rIdx);
                                code.Add(rVal);
                                return;
                            }
                            else
                            {
                                byte rRes = _tempReg++;
                                code.Add((byte)VmOpcode.ArrLoad);
                                code.Add(rRes);
                                code.Add(rLeft);
                                code.Add(rIdx);
                                rLeft = rRes;
                            }
                        }
                        else if (_current.Type == TokenType.Dot)
                        {
                            Advance();
                            string fName = _current.Value;
                            Expect(TokenType.Identifier);

                            if (Match(TokenType.Assign))
                            {
                                byte rVal = ParseExpression(code);
                                Expect(TokenType.Semicolon);
                                ushort sIdx = GetString(fName);

                                code.Add((byte)VmOpcode.SetField);
                                code.Add(rLeft);
                                code.Add((byte)(sIdx & 0xFF));
                                code.Add((byte)(sIdx >> 8));
                                code.Add(rVal);
                                return;
                            }
                            else if (Match(TokenType.LParen))
                            {
                                byte rBase = _tempReg;
                                byte rObjCopy = _tempReg++;
                                code.Add((byte)VmOpcode.Mov);
                                code.Add(rObjCopy);
                                code.Add(rLeft);

                                byte argc = 1;
                                while (_current.Type != TokenType.RParen && _current.Type != TokenType.Eof && _errorMsg == null)
                                {
                                    byte rArg = ParseExpression(code);
                                    if (rArg != rBase + argc)
                                    {
                                        code.Add((byte)VmOpcode.Mov);
                                        code.Add((byte)(rBase + argc));
                                        code.Add(rArg);
                                    }
                                    argc++;

                                    if (Match(TokenType.Comma))
                                    {
                                        if (_current.Type == TokenType.RParen)
                                        {
                                            Error("Unexpected comma");
                                            return;
                                        }
                                        continue;
                                    }
                                    break;
                                }
                                Expect(TokenType.RParen);

                                ushort sIdx = GetString(fName);
                                byte rFunc = _tempReg++;

                                code.Add((byte)VmOpcode.GetField);
                                code.Add(rFunc);
                                code.Add(rLeft);
                                code.Add((byte)(sIdx & 0xFF));
                                code.Add((byte)(sIdx >> 8));

                                code.Add((byte)VmOpcode.CallIndVoid);
                                code.Add(rFunc);
                                code.Add(rBase);
                                code.Add(argc);

                                _tempReg = rBase;
                                if (Match(TokenType.Semicolon)) return;
                            }
                            else
                            {
                                byte rRes = _tempReg++;
                                ushort sIdx = GetString(fName);

                                code.Add((byte)VmOpcode.GetField);
                                code.Add(rRes);
                                code.Add(rLeft);
                                code.Add((byte)(sIdx & 0xFF));
                                code.Add((byte)(sIdx >> 8));

                                rLeft = rRes;
                            }
                        }
                    }
                }
            }

            Error($"Unexpected token {_current.Value} at line {_current.Line}");
        }

        private static void ParseIfStatement(List<byte> code)
        {
            Advance();
            Expect(TokenType.LParen);
            byte rCond = ParseExpression(code);
            Expect(TokenType.RParen);

            code.Add((byte)VmOpcode.JmpFalse);
            code.Add(rCond);
            int jmpFalsePos = code.Count;
            code.Add(0);
            code.Add(0);

            Expect(TokenType.LBrace);
            while (_current.Type != TokenType.RBrace && _current.Type != TokenType.Eof && _errorMsg == null)
            {
                ParseStatement(code);
            }
            Expect(TokenType.RBrace);

            if (_current.Type == TokenType.Keyword && _current.Value == "else")
            {
                Advance();
                code.Add((byte)VmOpcode.Jmp);
                int jmpEndPos = code.Count;
                code.Add(0);
                code.Add(0);

                short skipOff = (short)(code.Count - (jmpFalsePos + 2));
                code[jmpFalsePos] = (byte)(skipOff & 0xFF);
                code[jmpFalsePos + 1] = (byte)(skipOff >> 8);

                if (_current.Type == TokenType.Keyword && _current.Value == "if")
                {
                    ParseIfStatement(code);
                }
                else
                {
                    Expect(TokenType.LBrace);
                    while (_current.Type != TokenType.RBrace && _current.Type != TokenType.Eof && _errorMsg == null)
                    {
                        ParseStatement(code);
                    }
                    Expect(TokenType.RBrace);
                }

                short endOff = (short)(code.Count - (jmpEndPos + 2));
                code[jmpEndPos] = (byte)(endOff & 0xFF);
                code[jmpEndPos + 1] = (byte)(endOff >> 8);
            }
            else
            {
                short skipOff = (short)(code.Count - (jmpFalsePos + 2));
                code[jmpFalsePos] = (byte)(skipOff & 0xFF);
                code[jmpFalsePos + 1] = (byte)(skipOff >> 8);
            }
        }

        private static byte ParseExpression(List<byte> code)
        {
            return ParseBitOr(code);
        }

        private static byte ParseBitOr(List<byte> code)
        {
            byte rLeft = ParseBitAnd(code);
            while (_current.Type == TokenType.Operator && _current.Value == "|" && _errorMsg == null)
            {
                Advance(); byte rRight = ParseBitAnd(code); byte rRes = _tempReg++;
                code.Add((byte)VmOpcode.BitOr); code.Add(rRes); code.Add(rLeft); code.Add(rRight); rLeft = rRes;
            }
            return rLeft;
        }

        private static byte ParseBitAnd(List<byte> code)
        {
            byte rLeft = ParseShift(code);
            while (_current.Type == TokenType.Operator && _current.Value == "&" && _errorMsg == null)
            {
                Advance(); byte rRight = ParseShift(code); byte rRes = _tempReg++;
                code.Add((byte)VmOpcode.BitAnd); code.Add(rRes); code.Add(rLeft); code.Add(rRight); rLeft = rRes;
            }
            return rLeft;
        }

        private static byte ParseShift(List<byte> code)
        {
            byte rLeft = ParseCompare(code);
            while (_current.Type == TokenType.Operator && (_current.Value == "<<" || _current.Value == ">>") && _errorMsg == null)
            {
                string op = _current.Value; Advance(); byte rRight = ParseCompare(code); byte rRes = _tempReg++;
                if (op == "<<") code.Add((byte)VmOpcode.Shl); else if (op == ">>") code.Add((byte)VmOpcode.Shr);
                code.Add(rRes); code.Add(rLeft); code.Add(rRight); rLeft = rRes;
            }
            return rLeft;
        }

        private static byte ParseCompare(List<byte> code)
        {
            byte rLeft = ParseTerm(code);
            while (_current.Type == TokenType.Operator &&
                  (_current.Value == "==" || _current.Value == "!=" ||
                   _current.Value == "<" || _current.Value == ">" ||
                   _current.Value == "<=" || _current.Value == ">=") && _errorMsg == null)
            {
                string op = _current.Value;
                Advance();
                byte rRight = ParseTerm(code);
                byte rRes = _tempReg++;

                if (op == "==") code.Add((byte)VmOpcode.Eq);
                else if (op == "!=") code.Add((byte)VmOpcode.Neq);
                else if (op == "<") code.Add((byte)VmOpcode.Lt);
                else if (op == "<=") code.Add((byte)VmOpcode.Le);
                else if (op == ">") code.Add((byte)VmOpcode.Gt);
                else if (op == ">=") code.Add((byte)VmOpcode.Ge);

                code.Add(rRes);
                code.Add(rLeft);
                code.Add(rRight);
                rLeft = rRes;
            }
            return rLeft;
        }

        private static byte ParseTerm(List<byte> code)
        {
            byte rLeft = ParseFactor(code);
            while (_current.Type == TokenType.Operator && (_current.Value == "+" || _current.Value == "-") && _errorMsg == null)
            {
                string op = _current.Value;
                Advance();
                byte rRight = ParseFactor(code);
                byte rRes = _tempReg++;

                if (op == "+") code.Add((byte)VmOpcode.Add);
                else if (op == "-") code.Add((byte)VmOpcode.Sub);

                code.Add(rRes);
                code.Add(rLeft);
                code.Add(rRight);
                rLeft = rRes;
            }
            return rLeft;
        }

        private static byte ParseFactor(List<byte> code)
        {
            byte rLeft = ParsePrimary(code);
            while (_current.Type == TokenType.Operator && (_current.Value == "*" || _current.Value == "/") && _errorMsg == null)
            {
                string op = _current.Value;
                Advance();
                byte rRight = ParsePrimary(code);
                byte rRes = _tempReg++;

                if (op == "*") code.Add((byte)VmOpcode.Mul);
                else if (op == "/") code.Add((byte)VmOpcode.Div);

                code.Add(rRes);
                code.Add(rLeft);
                code.Add(rRight);
                rLeft = rRes;
            }
            return rLeft;
        }

        private static float ParseFloatFast(string s)
        {
            float result = 0;
            float fraction = 0;
            float divisor = 1;
            bool isFraction = false;
            bool neg = false;
            int start = 0;

            if (s.StartsWith("-"))
            {
                neg = true;
                start = 1;
            }

            for (int i = start; i < s.Length; i++)
            {
                if (s[i] == '.' || s[i] == ',')
                {
                    isFraction = true;
                    continue;
                }
                if (isFraction)
                {
                    fraction = fraction * 10 + (s[i] - '0');
                    divisor *= 10;
                }
                else
                {
                    result = result * 10 + (s[i] - '0');
                }
            }
            result += fraction / divisor;
            return neg ? -result : result;
        }

        private static byte ParsePrimaryBase(List<byte> code)
        {
            if (_errorMsg != null) return 0;

            // --- Поддержка отрицательных чисел (Унарный минус) ---
            if (_current.Type == TokenType.Operator && _current.Value == "-")
            {
                Advance();
                byte rRight = ParsePrimary(code);

                // Генерируем `0 - rRight`
                byte rZero = _tempReg++;
                code.Add((byte)VmOpcode.LoadI32);
                code.Add(rZero);
                code.Add(0);
                code.Add(0);
                code.Add(0);
                code.Add(0);

                byte rRes = _tempReg++;
                code.Add((byte)VmOpcode.Sub);
                code.Add(rRes);
                code.Add(rZero);
                code.Add(rRight);

                return rRes;
            }

            if (_current.Type == TokenType.LParen)
            {
                Advance();
                byte rExpr = ParseExpression(code);
                Expect(TokenType.RParen);
                return rExpr;
            }

            if (_current.Type == TokenType.Keyword && _current.Value == "new")
            {
                Advance();
                if (Match(TokenType.LBracket))
                {
                    byte rSize = ParseExpression(code);
                    Expect(TokenType.RBracket);

                    byte rArr = _tempReg++;
                    code.Add((byte)VmOpcode.NewArray);
                    code.Add(rArr);
                    code.Add(rSize);
                    return rArr;
                }
                else if (_current.Type == TokenType.Keyword && _current.Value == "bytearray")
                {
                    Advance();
                    Expect(TokenType.LParen);
                    byte rSize = ParseExpression(code);
                    Expect(TokenType.RParen);

                    byte rArr = _tempReg++;
                    code.Add((byte)VmOpcode.NewByteArray);
                    code.Add(rArr);
                    code.Add(rSize);
                    return rArr;
                }
                else
                {
                    string cName = _current.Value;
                    Expect(TokenType.Identifier);
                    Expect(TokenType.LParen);
                    Expect(TokenType.RParen);

                    ushort fidx = GetFuncId(cName + "_ctor");
                    byte rDst = _tempReg++;

                    code.Add((byte)VmOpcode.Call);
                    code.Add(rDst);
                    code.Add((byte)(fidx & 0xFF));
                    code.Add((byte)(fidx >> 8));
                    code.Add(0);
                    code.Add(0);
                    return rDst;
                }
            }

            if (Match(TokenType.LBracket))
            {
                byte rArr = _tempReg++;
                code.Add((byte)VmOpcode.NewArrayEmpty);
                code.Add(rArr);

                while (_current.Type != TokenType.RBracket && _current.Type != TokenType.Eof && _errorMsg == null)
                {
                    byte rVal = ParseExpression(code);
                    code.Add((byte)VmOpcode.ArrPush);
                    code.Add(rArr);
                    code.Add(rVal);

                    if (Match(TokenType.Comma))
                    {
                        if (_current.Type == TokenType.RBracket)
                        {
                            Error("Unexpected comma");
                            return 0;
                        }
                        continue;
                    }
                    break;
                }
                Expect(TokenType.RBracket);
                return rArr;
            }

            if (_current.Type == TokenType.Keyword && _current.Value == "sys")
            {
                Advance();
                Expect(TokenType.LParen);
                ushort sysId = ushort.Parse(_current.Value);
                Advance();

                byte rBase = _tempReg;
                byte argc = 0;

                if (Match(TokenType.Comma))
                {
                    while (_current.Type != TokenType.RParen && _current.Type != TokenType.Eof && _errorMsg == null)
                    {
                        byte argReg = ParseExpression(code);
                        if (argReg != rBase + argc)
                        {
                            code.Add((byte)VmOpcode.Mov);
                            code.Add((byte)(rBase + argc));
                            code.Add(argReg);
                        }
                        argc++;

                        if (Match(TokenType.Comma))
                        {
                            if (_current.Type == TokenType.RParen)
                            {
                                Error("Unexpected comma");
                                return 0;
                            }
                            continue;
                        }
                        break;
                    }
                }
                Expect(TokenType.RParen);

                byte rDst = rBase;
                _tempReg = (byte)(rBase + argc);

                code.Add((byte)VmOpcode.CallNative);
                code.Add(rDst);
                code.Add((byte)(sysId & 0xFF));
                code.Add((byte)(sysId >> 8));
                code.Add(rBase);
                code.Add(argc);

                _tempReg = (byte)(rBase + 1);
                return rDst;
            }

            byte r = _tempReg++;

            if (_current.Type == TokenType.Number)
            {
                if (_current.Value.Contains(".") || _current.Value.Contains(","))
                {
                    float f = ParseFloatFast(_current.Value);
                    code.Add((byte)VmOpcode.LoadF64);
                    code.Add(r);
                    code.AddRange(BitConverter.GetBytes(f));
                }
                else
                {
                    int val = 0;
                    int.TryParse(_current.Value, out val);
                    code.Add((byte)VmOpcode.LoadI32);
                    code.Add(r);
                    code.Add((byte)(val & 0xFF));
                    code.Add((byte)(val >> 8));
                    code.Add((byte)(val >> 16));
                    code.Add((byte)(val >> 24));
                }
                Advance();
                return r;
            }

            if (_current.Type == TokenType.StringLiteral)
            {
                string val = _current.Value;
                Advance();

                if (val.Contains('$'))
                {
                    return CompileInterpolatedString(val, code);
                }

                ushort sIdx = GetString(val);
                code.Add((byte)VmOpcode.LoadStr);
                code.Add(r);
                code.Add((byte)(sIdx & 0xFF));
                code.Add((byte)(sIdx >> 8));
                return r;
            }

            if (_current.Type == TokenType.Keyword && (_current.Value == "true" || _current.Value == "false"))
            {
                code.Add(_current.Value == "true" ? (byte)VmOpcode.LoadTrue : (byte)VmOpcode.LoadFalse);
                code.Add(r);
                Advance();
                return r;
            }

            if (_current.Type == TokenType.Identifier || (_current.Type == TokenType.Keyword && _current.Value == "this"))
            {
                string name = _current.Value;
                Advance();

                if (_current.Type == TokenType.LParen)
                {
                    Advance();
                    byte rBase = _tempReg;
                    byte argc = 0;

                    while (_current.Type != TokenType.RParen && _current.Type != TokenType.Eof && _errorMsg == null)
                    {
                        byte argReg = ParseExpression(code);
                        if (argReg != rBase + argc)
                        {
                            code.Add((byte)VmOpcode.Mov);
                            code.Add((byte)(rBase + argc));
                            code.Add(argReg);
                        }
                        argc++;

                        if (Match(TokenType.Comma))
                        {
                            if (_current.Type == TokenType.RParen)
                            {
                                Error("Unexpected comma");
                                return 0;
                            }
                            continue;
                        }
                        break;
                    }
                    Expect(TokenType.RParen);

                    if (_funcArity.TryGetValue(name, out byte expected))
                    {
                        if (argc != expected)
                        {
                            Error($"Function '{name}' requires {expected} arguments, but {argc} were given.");
                            return 0;
                        }
                    }
                    else
                    {
                        Error($"Unknown function '{name}'");
                        return 0;
                    }

                    ushort fidx = GetFuncId(name);
                    byte rDst = rBase;
                    _tempReg = (byte)(rBase + argc);

                    code.Add((byte)VmOpcode.Call);
                    code.Add(rDst);
                    code.Add((byte)(fidx & 0xFF));
                    code.Add((byte)(fidx >> 8));
                    code.Add(rBase);
                    code.Add(argc);

                    _tempReg = (byte)(rBase + 1);
                    return rDst;
                }

                EmitVarLoad(name, r, code);
                return r;
            }

            Error($"Expected expression, got {_current.Type}");
            return r;
        }

        private static byte CompileInterpolatedString(string text, List<byte> code)
        {
            byte rFinal = _tempReg++;
            ushort emptyId = GetString("");
            code.Add((byte)VmOpcode.LoadStr);
            code.Add(rFinal);
            code.Add((byte)(emptyId & 0xFF));
            code.Add((byte)(emptyId >> 8));

            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '$' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
                {
                    i++;
                    int start = i;

                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.')) i++;

                    if (text[i - 1] == '.') i--;

                    string fullPath = text.Substring(start, i - start);
                    string[] parts = fullPath.Split('.'); // Разбиваем на this и name

                    // 1. Загружаем базовую переменную (например, this)
                    byte rVar = _tempReg++;
                    EmitVarLoad(parts[0], rVar, code);

                    // 2. Если есть точки, достаем поля (name, hp и т.д.)
                    for (int p = 1; p < parts.Length; p++)
                    {
                        byte rRes = _tempReg++;
                        ushort sIdx = GetString(parts[p]);

                        code.Add((byte)VmOpcode.GetField);
                        code.Add(rRes);
                        code.Add(rVar);
                        code.Add((byte)(sIdx & 0xFF));
                        code.Add((byte)(sIdx >> 8));

                        rVar = rRes;
                    }

                    byte rNewFinal = _tempReg++;
                    code.Add((byte)VmOpcode.Add);
                    code.Add(rNewFinal);
                    code.Add(rFinal);
                    code.Add(rVar);
                    rFinal = rNewFinal;
                }
                else
                {
                    // Обработка обычного текста
                    int start = i;
                    i++;
                    while (i < text.Length && text[i] != '$') i++;
                    string part = text.Substring(start, i - start);

                    byte rStr = _tempReg++;
                    ushort sIdx = GetString(part);
                    code.Add((byte)VmOpcode.LoadStr);
                    code.Add(rStr);
                    code.Add((byte)(sIdx & 0xFF));
                    code.Add((byte)(sIdx >> 8));

                    byte rNewFinal = _tempReg++;
                    code.Add((byte)VmOpcode.Add);
                    code.Add(rNewFinal);
                    code.Add(rFinal);
                    code.Add(rStr);
                    rFinal = rNewFinal;
                }
            }
            return rFinal;
        }

        private static byte ParsePrimary(List<byte> code)
        {
            byte rLeft = ParsePrimaryBase(code);

            while ((_current.Type == TokenType.LBracket || _current.Type == TokenType.Dot) && _errorMsg == null)
            {
                if (Match(TokenType.LBracket))
                {
                    byte rIdx = ParseExpression(code);
                    Expect(TokenType.RBracket);

                    byte rRes = _tempReg++;
                    code.Add((byte)VmOpcode.ArrLoad);
                    code.Add(rRes);
                    code.Add(rLeft);
                    code.Add(rIdx);
                    rLeft = rRes;
                }
                else if (Match(TokenType.Dot))
                {
                    string fName = _current.Value;
                    Expect(TokenType.Identifier);

                    if (Match(TokenType.LParen))
                    {
                        byte rBase = _tempReg;
                        byte rObjCopy = _tempReg++;
                        code.Add((byte)VmOpcode.Mov);
                        code.Add(rObjCopy);
                        code.Add(rLeft);

                        byte argc = 1;
                        while (_current.Type != TokenType.RParen && _current.Type != TokenType.Eof && _errorMsg == null)
                        {
                            byte rArg = ParseExpression(code);
                            if (rArg != rBase + argc)
                            {
                                code.Add((byte)VmOpcode.Mov);
                                code.Add((byte)(rBase + argc));
                                code.Add(rArg);
                            }
                            argc++;

                            if (Match(TokenType.Comma))
                            {
                                if (_current.Type == TokenType.RParen)
                                {
                                    Error("Unexpected comma");
                                    return 0;
                                }
                                continue;
                            }
                            break;
                        }
                        Expect(TokenType.RParen);

                        ushort sIdx = GetString(fName);
                        byte rFunc = _tempReg++;

                        code.Add((byte)VmOpcode.GetField);
                        code.Add(rFunc);
                        code.Add(rLeft);
                        code.Add((byte)(sIdx & 0xFF));
                        code.Add((byte)(sIdx >> 8));

                        byte rDst = rBase;
                        code.Add((byte)VmOpcode.CallInd);
                        code.Add(rDst);
                        code.Add(rFunc);
                        code.Add(rBase);
                        code.Add(argc);

                        _tempReg = (byte)(rBase + 1);
                        rLeft = rDst;
                    }
                    else
                    {
                        byte rRes = _tempReg++;
                        ushort sIdx = GetString(fName);

                        code.Add((byte)VmOpcode.GetField);
                        code.Add(rRes);
                        code.Add(rLeft);
                        code.Add((byte)(sIdx & 0xFF));
                        code.Add((byte)(sIdx >> 8));
                        rLeft = rRes;
                    }
                }
            }
            return rLeft;
        }

        private static byte[] BuildModule(byte[] code)
        {
            List<byte> payload = new List<byte>();
            ushort strCount = (ushort)_strings.Count;
            ushort funcCount = (ushort)_functions.Count;

            payload.Add((byte)(strCount & 0xFF));
            payload.Add((byte)(strCount >> 8));
            payload.Add(0);
            payload.Add(0);

            payload.Add((byte)(funcCount & 0xFF));
            payload.Add((byte)(funcCount >> 8));
            payload.Add(0);
            payload.Add(0);
            payload.Add(0);
            payload.Add(0);

            foreach (var s in _strings)
            {
                byte[] strBytes = Encoding.ASCII.GetBytes(s);
                ushort len = (ushort)strBytes.Length;
                payload.Add((byte)(len & 0xFF));
                payload.Add((byte)(len >> 8));
                payload.AddRange(strBytes);
            }

            payload.AddRange(code);

            List<byte> bvx = new List<byte>();
            bvx.AddRange(Encoding.ASCII.GetBytes("BVX-Vm"));
            bvx.Add(1);
            bvx.Add(0);
            bvx.Add(0);

            int size = payload.Count;
            bvx.Add((byte)(size & 0xFF));
            bvx.Add((byte)((size >> 8) & 0xFF));
            bvx.Add((byte)((size >> 16) & 0xFF));
            bvx.Add((byte)((size >> 24) & 0xFF));

            bvx.AddRange(payload);
            return bvx.ToArray();
        }
    }
}