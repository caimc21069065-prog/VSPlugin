using System;
using System.Collections.Generic;
using System.Text;

namespace CtrlTNav.Indexing.Cpp
{
    /// <summary>
    /// Tolerant hand-written C/C++ lexer. It never throws on malformed input: unterminated comments,
    /// strings, raw strings, char literals, stray characters and truncated files are consumed as well as
    /// possible and lexing continues.
    /// </summary>
    /// <remarks>
    /// Preprocessor directives are handled inside the lexer:
    /// <list type="bullet">
    /// <item><description><c>#define</c> produces a <see cref="CppTokenKind.Macro"/> pseudo token carrying
    /// the macro name (function-like macros additionally record their parameter text).</description></item>
    /// <item><description><c>#if 0</c> regions are skipped (nested <c>#if</c>/<c>#else</c>/<c>#endif</c>
    /// tracking is tolerant; unknown conditions index both branches so no real code is lost).</description></item>
    /// <item><description>Every other directive is skipped until the end of its logical line.</description></item>
    /// </list>
    /// </remarks>
    internal sealed class CppTokenizer
    {
        private const int MaxTokens = 2000000;
        private const int MaxRawStringDelimiter = 16;
        private const int MaxMacroSignature = 256;

        private readonly string _text;
        private readonly int _length;
        private readonly List<CppToken> _tokens = new List<CppToken>(1024);
        private readonly Dictionary<int, string> _macroSignatures = new Dictionary<int, string>();
        private readonly List<ConditionFrame> _conditions = new List<ConditionFrame>();
        private readonly StringBuilder _scratch = new StringBuilder(256);

        private int _pos;
        private int _line = 1;
        private int _lineStart;
        private bool _lineHasToken;

        private CppTokenizer(string text)
        {
            _text = text;
            _length = text.Length;
        }

        public static TokenizedFile Tokenize(string? text)
        {
            var tokenizer = new CppTokenizer(text ?? string.Empty);
            tokenizer.Run();
            return new TokenizedFile(tokenizer._tokens, tokenizer._macroSignatures);
        }

        private struct ConditionFrame
        {
            /// <summary>True when the enclosing region is already disabled.</summary>
            public bool ParentDisabled;

            /// <summary><c>true</c>/<c>false</c> for a literal <c>#if 1</c>/<c>#if 0</c>, null when unknown.</summary>
            public bool? KnownValue;

            public bool InElse;

            /// <summary>Effective disabled state of the currently active branch.</summary>
            public bool CurrentDisabled;
        }

        private bool IsDisabled
        {
            get
            {
                if (_conditions.Count == 0)
                {
                    return false;
                }

                return _conditions[_conditions.Count - 1].CurrentDisabled;
            }
        }

        private void Run()
        {
            while (_pos < _length)
            {
                if (_tokens.Count >= MaxTokens)
                {
                    break;
                }

                if (IsDisabled)
                {
                    SkipDisabledContent();
                    continue;
                }

                char c = _text[_pos];

                // Whitespace (a UTF-8 BOM can leak in when content is supplied by a test or another host).
                if (c == ' ' || c == '\t' || c == '\f' || c == '\v' || c == '\uFEFF')
                {
                    _pos++;
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    ConsumeNewline();
                    continue;
                }

                // Backslash-newline line splice: behave like whitespace.
                if (c == '\\' && IsNewlineAt(_pos + 1))
                {
                    _pos++;
                    ConsumeNewline();
                    continue;
                }

                if (c == '/' && PeekChar(1) == '/')
                {
                    SkipLineComment();
                    continue;
                }

                if (c == '/' && PeekChar(1) == '*')
                {
                    SkipBlockComment();
                    continue;
                }

                int tokenStart = _pos;
                int tokenLine = _line;
                int tokenColumn = _pos - _lineStart + 1;

                if (c == '#' && !_lineHasToken)
                {
                    HandleDirective();
                    continue;
                }

                if (IsIdentifierStart(c))
                {
                    int identifierLength = ReadIdentifierSpan();
                    if (_pos < _length)
                    {
                        char next = _text[_pos];
                        if (next == '"' && IsStringPrefix(tokenStart, identifierLength))
                        {
                            bool raw = _text[tokenStart + identifierLength - 1] == 'R';
                            ReadStringLiteral(raw);
                            AddToken(CppTokenKind.StringLiteral, tokenStart, _pos - tokenStart, tokenLine, tokenColumn);
                            continue;
                        }

                        if (next == '\'' && IsCharPrefix(tokenStart, identifierLength))
                        {
                            ReadCharLiteral();
                            AddToken(CppTokenKind.CharLiteral, tokenStart, _pos - tokenStart, tokenLine, tokenColumn);
                            continue;
                        }
                    }

                    AddToken(CppTokenKind.Identifier, tokenStart, identifierLength, tokenLine, tokenColumn);
                    continue;
                }

                if (IsDigit(c) || (c == '.' && IsDigit(PeekChar(1))))
                {
                    ReadNumber();
                    AddToken(CppTokenKind.Number, tokenStart, _pos - tokenStart, tokenLine, tokenColumn);
                    continue;
                }

                if (c == '"')
                {
                    ReadStringLiteral(false);
                    AddToken(CppTokenKind.StringLiteral, tokenStart, _pos - tokenStart, tokenLine, tokenColumn);
                    continue;
                }

                if (c == '\'')
                {
                    ReadCharLiteral();
                    AddToken(CppTokenKind.CharLiteral, tokenStart, _pos - tokenStart, tokenLine, tokenColumn);
                    continue;
                }

                int operatorLength = MatchOperator();
                if (operatorLength <= 0)
                {
                    operatorLength = 1;
                }

                char first = c;
                _pos += operatorLength;
                AddToken(
                    IsKnownSinglePunctuation(first) ? CppTokenKind.Punctuation : CppTokenKind.Unknown,
                    tokenStart,
                    operatorLength,
                    tokenLine,
                    tokenColumn);
            }

            int eofLine = _line;
            int eofColumn = _pos - _lineStart + 1;
            _tokens.Add(new CppToken(CppTokenKind.EndOfFile, _pos, 0, eofLine, eofColumn));
        }

        // ---------------------------------------------------------------------------------------------
        // Token helpers
        // ---------------------------------------------------------------------------------------------

        private void AddToken(CppTokenKind kind, int start, int length, int line, int column)
        {
            if (length <= 0)
            {
                return;
            }

            _tokens.Add(new CppToken(kind, start, length, line, column));
            _lineHasToken = true;
        }

        private char PeekChar(int offset)
        {
            int index = _pos + offset;
            return index >= 0 && index < _length ? _text[index] : '\0';
        }

        private bool IsNewlineAt(int index)
        {
            if (index < 0 || index >= _length)
            {
                return false;
            }

            char c = _text[index];
            return c == '\r' || c == '\n';
        }

        private void ConsumeNewline()
        {
            if (_pos >= _length)
            {
                return;
            }

            char c = _text[_pos];
            _pos++;
            if (c == '\r' && _pos < _length && _text[_pos] == '\n')
            {
                _pos++;
            }

            _line++;
            _lineStart = _pos;
            _lineHasToken = false;
        }

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        private static bool IsIdentifierStart(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == '$' || c >= 128;
        }

        private static bool IsIdentifierPart(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '$' || c >= 128;
        }

        private int ReadIdentifierSpan()
        {
            int start = _pos;
            while (_pos < _length && IsIdentifierPart(_text[_pos]))
            {
                _pos++;
            }

            return _pos - start;
        }

        private bool IsStringPrefix(int start, int length)
        {
            if (length == 1)
            {
                char c = _text[start];
                return c == 'L' || c == 'u' || c == 'U' || c == 'R';
            }

            if (length == 2)
            {
                return SequenceEquals(start, 2, "u8") || SequenceEquals(start, 2, "LR") ||
                       SequenceEquals(start, 2, "uR") || SequenceEquals(start, 2, "UR");
            }

            if (length == 3)
            {
                return SequenceEquals(start, 3, "u8R");
            }

            return false;
        }

        private bool IsCharPrefix(int start, int length)
        {
            if (length != 1)
            {
                return false;
            }

            char c = _text[start];
            return c == 'L' || c == 'u' || c == 'U';
        }

        private bool SequenceEquals(int start, int length, string value)
        {
            if (length != value.Length || start + length > _length)
            {
                return false;
            }

            return string.CompareOrdinal(_text, start, value, 0, length) == 0;
        }

        private static bool IsKnownSinglePunctuation(char c)
        {
            switch (c)
            {
                case '(':
                case ')':
                case '{':
                case '}':
                case '[':
                case ']':
                case ';':
                case ':':
                case ',':
                case '.':
                case '?':
                case '=':
                case '+':
                case '-':
                case '*':
                case '/':
                case '%':
                case '&':
                case '|':
                case '^':
                case '!':
                case '~':
                case '<':
                case '>':
                case '#':
                case '\\':
                case '@':
                    return true;
                default:
                    return false;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Literals
        // ---------------------------------------------------------------------------------------------

        private void ReadStringLiteral(bool raw)
        {
            // _pos points at the opening quote.
            _pos++;

            if (raw)
            {
                if (TryReadRawStringBody())
                {
                    return;
                }

                // Not a well-formed raw string: fall through and treat the remainder as a normal string.
            }

            while (_pos < _length)
            {
                char c = _text[_pos];
                if (c == '\\')
                {
                    _pos++;
                    if (_pos < _length && IsNewlineAt(_pos))
                    {
                        ConsumeNewline();
                    }
                    else if (_pos < _length)
                    {
                        _pos++;
                    }

                    continue;
                }

                if (c == '"')
                {
                    _pos++;
                    return;
                }

                if (c == '\r' || c == '\n')
                {
                    // Unterminated string literal: stop before the newline so following lines still lex.
                    return;
                }

                _pos++;
            }
        }

        /// <summary>Reads <c>R"delim( ... )delim"</c>. Assumes the opening quote was consumed.</summary>
        private bool TryReadRawStringBody()
        {
            int delimiterStart = _pos;
            int delimiterEnd = -1;
            int probe = _pos;
            while (probe < _length && probe - delimiterStart <= MaxRawStringDelimiter)
            {
                char c = _text[probe];
                if (c == '(')
                {
                    delimiterEnd = probe;
                    break;
                }

                if (c == '"' || c == ')' || c == '\\' || c == '\r' || c == '\n' || c == ' ' || c == '\t')
                {
                    return false;
                }

                probe++;
            }

            if (delimiterEnd < 0)
            {
                return false;
            }

            string delimiter = _text.Substring(delimiterStart, delimiterEnd - delimiterStart);
            _pos = delimiterEnd + 1;

            while (_pos < _length)
            {
                char c = _text[_pos];
                if (c == ')' && MatchesDelimiterTerminator(delimiter))
                {
                    _pos += delimiter.Length + 2;
                    return true;
                }

                if (c == '\r' || c == '\n')
                {
                    ConsumeNewline();
                    continue;
                }

                _pos++;
            }

            return true;
        }

        private bool MatchesDelimiterTerminator(string delimiter)
        {
            int index = _pos + 1;
            if (index + delimiter.Length >= _length + 1)
            {
                return false;
            }

            if (index + delimiter.Length + 1 > _length)
            {
                return false;
            }

            for (int i = 0; i < delimiter.Length; i++)
            {
                if (_text[index + i] != delimiter[i])
                {
                    return false;
                }
            }

            return _text[index + delimiter.Length] == '"';
        }

        private void ReadCharLiteral()
        {
            _pos++; // opening quote
            while (_pos < _length)
            {
                char c = _text[_pos];
                if (c == '\\')
                {
                    _pos++;
                    if (_pos < _length && IsNewlineAt(_pos))
                    {
                        ConsumeNewline();
                    }
                    else if (_pos < _length)
                    {
                        _pos++;
                    }

                    continue;
                }

                if (c == '\'')
                {
                    _pos++;
                    return;
                }

                if (c == '\r' || c == '\n')
                {
                    return;
                }

                _pos++;
            }
        }

        private void ReadNumber()
        {
            while (_pos < _length)
            {
                char c = _text[_pos];
                if (IsDigit(c) || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == '\'')
                {
                    _pos++;
                    continue;
                }

                if (c == '.')
                {
                    if (_pos + 1 < _length && IsDigit(_text[_pos + 1]))
                    {
                        _pos++;
                        continue;
                    }

                    break;
                }

                if ((c == '+' || c == '-') && _pos > 0)
                {
                    char previous = _text[_pos - 1];
                    if (previous == 'e' || previous == 'E' || previous == 'p' || previous == 'P')
                    {
                        _pos++;
                        continue;
                    }
                }

                break;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Comments
        // ---------------------------------------------------------------------------------------------

        private void SkipLineComment()
        {
            _pos += 2;
            while (_pos < _length && !IsNewlineAt(_pos))
            {
                _pos++;
            }
        }

        private void SkipBlockComment()
        {
            _pos += 2;
            while (_pos < _length)
            {
                char c = _text[_pos];
                if (c == '*' && PeekChar(1) == '/')
                {
                    _pos += 2;
                    return;
                }

                if (IsNewlineAt(_pos))
                {
                    ConsumeNewline();
                    continue;
                }

                _pos++;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Operators
        // ---------------------------------------------------------------------------------------------

        private int MatchOperator()
        {
            // Longest match first. Only a handful of multi-character operators matter to the parser.
            if (MatchesAt("<=>") || MatchesAt("->*") || MatchesAt("<<=") || MatchesAt(">>=") || MatchesAt("..."))
            {
                return 3;
            }

            if (MatchesAt("::") || MatchesAt("->") || MatchesAt("++") || MatchesAt("--") ||
                MatchesAt("<<") || MatchesAt(">>") || MatchesAt("<=") || MatchesAt(">=") ||
                MatchesAt("==") || MatchesAt("!=") || MatchesAt("&&") || MatchesAt("||") ||
                MatchesAt("+=") || MatchesAt("-=") || MatchesAt("*=") || MatchesAt("/=") ||
                MatchesAt("%=") || MatchesAt("&=") || MatchesAt("|=") || MatchesAt("^=") ||
                MatchesAt(".*") || MatchesAt("##"))
            {
                return 2;
            }

            return 0;
        }

        private bool MatchesAt(string value)
        {
            if (_pos + value.Length > _length)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (_text[_pos + i] != value[i])
                {
                    return false;
                }
            }

            return true;
        }

        // ---------------------------------------------------------------------------------------------
        // Preprocessor
        // ---------------------------------------------------------------------------------------------

        private void HandleDirective()
        {
            // _pos points at '#'.
            _pos++;
            while (_pos < _length && (_text[_pos] == ' ' || _text[_pos] == '\t'))
            {
                _pos++;
            }

            string directive = ReadDirectiveWord();
            switch (directive)
            {
                case "define":
                    if (IsDisabled)
                    {
                        SkipLogicalLine();
                    }
                    else
                    {
                        ReadDefineDirective();
                    }

                    break;

                case "if":
                    PushCondition(EvaluateSimpleCondition(ReadRestOfLogicalLine()));
                    break;

                case "ifdef":
                case "ifndef":
                    ReadRestOfLogicalLine();
                    PushCondition(null);
                    break;

                case "elif":
                    ReadRestOfLogicalLine();
                    HandleElif();
                    break;

                case "else":
                    ReadRestOfLogicalLine();
                    HandleElse();
                    break;

                case "endif":
                    ReadRestOfLogicalLine();
                    PopCondition();
                    break;

                default:
                    SkipLogicalLine();
                    break;
            }
        }

        private string ReadDirectiveWord()
        {
            int start = _pos;
            while (_pos < _length && IsIdentifierPart(_text[_pos]))
            {
                _pos++;
            }

            if (_pos == start)
            {
                return string.Empty;
            }

            return _text.Substring(start, _pos - start);
        }

        private void ReadDefineDirective()
        {
            while (_pos < _length && (_text[_pos] == ' ' || _text[_pos] == '\t'))
            {
                _pos++;
            }

            int nameStart = _pos;
            if (nameStart >= _length || !IsIdentifierStart(_text[nameStart]))
            {
                SkipLogicalLine();
                return;
            }

            while (_pos < _length && IsIdentifierPart(_text[_pos]))
            {
                _pos++;
            }

            int nameLength = _pos - nameStart;
            int nameLine = _line;
            int nameColumn = nameStart - _lineStart + 1;
            bool functionLike = _pos < _length && _text[_pos] == '(';

            string signature = string.Empty;
            if (functionLike)
            {
                signature = ReadMacroParameterList();
            }

            _tokens.Add(new CppToken(CppTokenKind.Macro, nameStart, nameLength, nameLine, nameColumn));
            _lineHasToken = true;
            if (signature.Length > 0)
            {
                _macroSignatures[nameStart] = signature;
            }

            SkipLogicalLine();
        }

        private string ReadMacroParameterList()
        {
            var signature = new StringBuilder(32);
            int depth = 0;
            while (_pos < _length)
            {
                char c = _text[_pos];
                if (c == '\\' && IsNewlineAt(_pos + 1))
                {
                    _pos++;
                    ConsumeNewline();
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    // Malformed: the parameter list is not closed on the logical line.
                    break;
                }

                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }

                _pos++;
                signature.Append(c);
                if (depth == 0)
                {
                    break;
                }

                if (signature.Length > MaxMacroSignature)
                {
                    break;
                }
            }

            return signature.ToString();
        }

        private string ReadRestOfLogicalLine()
        {
            _scratch.Clear();
            while (_pos < _length)
            {
                char c = _text[_pos];
                if (c == '\r' || c == '\n')
                {
                    ConsumeNewline();
                    break;
                }

                if (c == '\\' && IsNewlineAt(_pos + 1))
                {
                    _pos++;
                    ConsumeNewline();
                    _scratch.Append(' ');
                    continue;
                }

                _scratch.Append(c);
                _pos++;
            }

            return _scratch.ToString();
        }

        private static bool? EvaluateSimpleCondition(string? expression)
        {
            if (expression == null)
            {
                return null;
            }

            string value = expression.Trim();
            int comment = value.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                value = value.Substring(0, comment).Trim();
            }

            int blockComment = value.IndexOf("/*", StringComparison.Ordinal);
            if (blockComment >= 0)
            {
                value = value.Substring(0, blockComment).Trim();
            }

            if (value.Length == 0)
            {
                return null;
            }

            if (value == "0")
            {
                return false;
            }

            if (value == "1")
            {
                return true;
            }

            return null;
        }

        private void PushCondition(bool? knownValue)
        {
            bool parentDisabled = IsDisabled;
            var frame = new ConditionFrame
            {
                ParentDisabled = parentDisabled,
                KnownValue = knownValue,
                InElse = false,
            };
            frame.CurrentDisabled = parentDisabled || (knownValue.HasValue && !knownValue.Value);
            _conditions.Add(frame);
        }

        private void HandleElse()
        {
            if (_conditions.Count == 0)
            {
                return;
            }

            ConditionFrame frame = _conditions[_conditions.Count - 1];
            frame.InElse = true;
            frame.CurrentDisabled = frame.ParentDisabled || (frame.KnownValue.HasValue && frame.KnownValue.Value);
            _conditions[_conditions.Count - 1] = frame;
        }

        private void HandleElif()
        {
            if (_conditions.Count == 0)
            {
                return;
            }

            ConditionFrame frame = _conditions[_conditions.Count - 1];
            // Unknown #elif conditions are treated as enabled so no real declarations are lost.
            frame.CurrentDisabled = frame.ParentDisabled;
            _conditions[_conditions.Count - 1] = frame;
        }

        private void PopCondition()
        {
            if (_conditions.Count > 0)
            {
                _conditions.RemoveAt(_conditions.Count - 1);
            }
        }

        private void SkipLogicalLine()
        {
            while (_pos < _length)
            {
                char c = _text[_pos];
                if (c == '\r' || c == '\n')
                {
                    ConsumeNewline();
                    return;
                }

                if (c == '\\' && IsNewlineAt(_pos + 1))
                {
                    _pos++;
                    ConsumeNewline();
                    continue;
                }

                if (c == '/' && PeekChar(1) == '*')
                {
                    SkipBlockComment();
                    continue;
                }

                if (c == '/' && PeekChar(1) == '/')
                {
                    while (_pos < _length && !IsNewlineAt(_pos))
                    {
                        _pos++;
                    }

                    continue;
                }

                _pos++;
            }
        }

        private void SkipDisabledContent()
        {
            while (_pos < _length && IsDisabled)
            {
                int probe = _pos;
                while (probe < _length &&
                       (_text[probe] == ' ' || _text[probe] == '\t' || _text[probe] == '\f' || _text[probe] == '\v'))
                {
                    probe++;
                }

                if (probe < _length && _text[probe] == '#')
                {
                    _pos = probe;
                    HandleDirective();
                    continue;
                }

                while (_pos < _length && !IsNewlineAt(_pos))
                {
                    _pos++;
                }

                if (_pos < _length)
                {
                    ConsumeNewline();
                }
            }
        }
    }
}
