using System;
using System.Collections.Generic;

namespace CtrlTNav.Indexing.Cpp
{
    /// <summary>Kind of a lexical token produced by <see cref="CppTokenizer"/>.</summary>
    internal enum CppTokenKind : byte
    {
        /// <summary>Unrecognized input. Emitted only for single unknown characters.</summary>
        Unknown = 0,

        Identifier = 1,
        Number = 2,
        StringLiteral = 3,
        CharLiteral = 4,
        Punctuation = 5,

        /// <summary>Pseudo token naming a <c>#define</c> macro; carries the macro name span.</summary>
        Macro = 6,

        EndOfFile = 7,
    }

    /// <summary>
    /// A lexical token with absolute source offsets and 1-based line/column of its first character.
    /// Tokens are value types so the token stream stays cheap for large files.
    /// </summary>
    internal readonly struct CppToken
    {
        public CppToken(CppTokenKind kind, int start, int length, int line, int column)
        {
            Kind = kind;
            Start = start;
            Length = length;
            Line = line;
            Column = column;
        }

        public CppTokenKind Kind { get; }

        /// <summary>Offset of the first character inside the source string.</summary>
        public int Start { get; }

        public int Length { get; }

        /// <summary>Offset one past the last character.</summary>
        public int End
        {
            get { return Start + Length; }
        }

        /// <summary>1-based line of the first character.</summary>
        public int Line { get; }

        /// <summary>1-based column (character count) of the first character.</summary>
        public int Column { get; }

        public override string ToString()
        {
            return Kind + "[" + Length + "]@" + Line + ":" + Column;
        }
    }

    /// <summary>Result of tokenizing one file.</summary>
    internal sealed class TokenizedFile
    {
        public TokenizedFile(List<CppToken> tokens, Dictionary<int, string> macroSignatures)
        {
            Tokens = tokens;
            MacroSignatures = macroSignatures;
        }

        public List<CppToken> Tokens { get; }

        /// <summary>
        /// Function-like macro parameter text keyed by the <see cref="CppToken.Start"/> offset of the
        /// corresponding <see cref="CppTokenKind.Macro"/> token. Object-like macros are not present.
        /// </summary>
        public Dictionary<int, string> MacroSignatures { get; }
    }
}
