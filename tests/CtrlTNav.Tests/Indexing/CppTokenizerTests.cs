using System;
using System.Collections.Generic;
using CtrlTNav.Indexing.Cpp;
using Xunit;

namespace CtrlTNav.Tests.Indexing
{
    public sealed class CppTokenizerTests
    {
        private static List<CppToken> Tokenize(string code)
        {
            return CppTokenizer.Tokenize(code).Tokens;
        }

        private static List<string> Texts(string code, CppTokenKind kind)
        {
            var result = new List<string>();
            foreach (CppToken token in Tokenize(code))
            {
                if (token.Kind == kind)
                {
                    result.Add(code.Substring(token.Start, token.Length));
                }
            }

            return result;
        }

        private static string TextAt(string code, CppToken token)
        {
            return code.Substring(token.Start, token.Length);
        }

        [Fact]
        public void TokenizesIdentifiersNumbersPunctuationAndPositions()
        {
            const string code = "int value = 42;";
            List<CppToken> tokens = Tokenize(code);

            Assert.Equal(6, tokens.Count);
            Assert.Equal(CppTokenKind.Identifier, tokens[0].Kind);
            Assert.Equal(1, tokens[0].Line);
            Assert.Equal(1, tokens[0].Column);
            Assert.Equal("int", TextAt(code, tokens[0]));

            Assert.Equal("value", TextAt(code, tokens[1]));
            Assert.Equal(5, tokens[1].Column);

            Assert.Equal(CppTokenKind.Punctuation, tokens[2].Kind);
            Assert.Equal("=", TextAt(code, tokens[2]));
            Assert.Equal(11, tokens[2].Column);

            Assert.Equal(CppTokenKind.Number, tokens[3].Kind);
            Assert.Equal(13, tokens[3].Column);

            Assert.Equal(";", TextAt(code, tokens[4]));
            Assert.Equal(CppTokenKind.EndOfFile, tokens[5].Kind);
        }

        [Fact]
        public void PositionsAreOneBasedAcrossCrLfAndTabs()
        {
            const string code = "int x;\r\n  foo();\n\tbar";
            List<CppToken> tokens = Tokenize(code);

            CppToken x = tokens[1];
            Assert.Equal(1, x.Line);
            Assert.Equal(5, x.Column);

            CppToken foo = tokens[3];
            Assert.Equal("foo", TextAt(code, foo));
            Assert.Equal(2, foo.Line);
            Assert.Equal(3, foo.Column);

            CppToken bar = tokens[7];
            Assert.Equal("bar", TextAt(code, bar));
            Assert.Equal(3, bar.Line);
            Assert.Equal(2, bar.Column);
        }

        [Fact]
        public void SkipsLineAndBlockComments()
        {
            const string code = "int a; // one\n/* two */ int b;";
            Assert.Equal(new[] { "int", "a", "int", "b" }, Texts(code, CppTokenKind.Identifier));
        }

        [Fact]
        public void UnterminatedBlockCommentStopsAtEndOfFileWithoutThrowing()
        {
            const string code = "int a;\n/* never closed\nint b;";
            List<CppToken> tokens = Tokenize(code);
            var words = new List<string>();
            foreach (CppToken token in tokens)
            {
                if (token.Kind == CppTokenKind.Identifier)
                {
                    words.Add(TextAt(code, token));
                }
            }

            Assert.Equal(new[] { "int", "a" }, words);
        }

        [Fact]
        public void StringAndCharLiteralsAreSingleTokens()
        {
            const string code = "const char* s = \"text\"; char c = 'x'; auto w = L\"wide\"; auto u = u8\"utf\";";
            Assert.Equal(3, Texts(code, CppTokenKind.StringLiteral).Count);
            Assert.Equal(1, Texts(code, CppTokenKind.CharLiteral).Count);
        }

        [Fact]
        public void RawStringSpansLinesAndKeepsFollowingLineNumbers()
        {
            const string code = "const char* s = R\"(hello\nworld)\";\nint after;";
            List<CppToken> tokens = Tokenize(code);
            Assert.Equal(1, Texts(code, CppTokenKind.StringLiteral).Count);

            bool found = false;
            foreach (CppToken token in tokens)
            {
                if (token.Kind == CppTokenKind.Identifier && TextAt(code, token) == "after")
                {
                    Assert.Equal(3, token.Line);
                    found = true;
                }
            }

            Assert.True(found, "identifier after the raw string was not tokenized");
        }

        [Fact]
        public void RawStringWithCustomDelimiterIsHandled()
        {
            const string code = "auto s = R\"tag(a)\" still raw)tag\";\nint x;";
            List<CppToken> tokens = Tokenize(code);
            Assert.Equal(1, Texts(code, CppTokenKind.StringLiteral).Count);
            bool found = false;
            foreach (CppToken token in tokens)
            {
                if (token.Kind == CppTokenKind.Identifier && TextAt(code, token) == "x")
                {
                    Assert.Equal(2, token.Line);
                    found = true;
                }
            }

            Assert.True(found);
        }

        [Fact]
        public void UnterminatedRawStringConsumesRestWithoutThrowing()
        {
            const string code = "auto s = R\"(never closed\nint x;";
            List<CppToken> tokens = Tokenize(code);
            Assert.Contains(tokens, token => token.Kind == CppTokenKind.StringLiteral);
            Assert.Equal(CppTokenKind.EndOfFile, tokens[tokens.Count - 1].Kind);
        }

        [Fact]
        public void UnterminatedStringStopsAtNewline()
        {
            const string code = "const char* s = \"never closed\nint x;";
            Assert.Contains(Tokenize(code), token =>
                token.Kind == CppTokenKind.Identifier && TextAt(code, token) == "x");
        }

        [Fact]
        public void NumbersAreSingleTokens()
        {
            const string code = "0x1F 1'000 3.14f 1e-5 42ULL";
            Assert.Equal(5, Texts(code, CppTokenKind.Number).Count);
        }

        [Fact]
        public void BackslashNewlineActsAsWhitespace()
        {
            const string code = "int a = 1 + \\\n2;";
            List<CppToken> tokens = Tokenize(code);
            CppToken two = tokens[tokens.Count - 3];
            Assert.Equal(CppTokenKind.Number, two.Kind);
            Assert.Equal("2", TextAt(code, two));
            Assert.Equal(2, two.Line);
        }

        [Fact]
        public void DirectivesAreSkippedButDefinesEmitMacroTokens()
        {
            const string code = "#include <vector>\n#pragma once\n#define ONLY_MACRO 1\n#undef ONLY_MACRO\n#ifdef X\n#endif\n";
            List<CppToken> macroTokens = new List<CppToken>();
            foreach (CppToken token in Tokenize(code))
            {
                if (token.Kind == CppTokenKind.Macro)
                {
                    macroTokens.Add(token);
                }
            }

            Assert.Single(macroTokens);
            Assert.Equal("ONLY_MACRO", TextAt(code, macroTokens[0]));
            Assert.Equal(3, macroTokens[0].Line);
            Assert.Equal(9, macroTokens[0].Column);
        }

        [Fact]
        public void FunctionLikeMacroRecordsSignature()
        {
            const string code = "#define MAX(a, b) ((a) > (b) ? (a) : (b))";
            TokenizedFile tokenized = CppTokenizer.Tokenize(code);
            CppToken macro = default;
            bool found = false;
            foreach (CppToken token in tokenized.Tokens)
            {
                if (token.Kind == CppTokenKind.Macro)
                {
                    macro = token;
                    found = true;
                    break;
                }
            }

            Assert.True(found);
            Assert.Equal("MAX", code.Substring(macro.Start, macro.Length));
            Assert.True(tokenized.MacroSignatures.ContainsKey(macro.Start));
            Assert.Equal("(a, b)", tokenized.MacroSignatures[macro.Start]);
        }

        [Fact]
        public void MacroLineContinuationDoesNotSwallowFollowingCode()
        {
            const string code = "#define LONG_MACRO(x) \\\n    ((x) + 1)\nint after;";
            List<CppToken> tokens = Tokenize(code);
            bool macroFound = false;
            bool afterFound = false;
            foreach (CppToken token in tokens)
            {
                if (token.Kind == CppTokenKind.Macro)
                {
                    macroFound = true;
                    Assert.Equal("LONG_MACRO", TextAt(code, token));
                }

                if (token.Kind == CppTokenKind.Identifier && TextAt(code, token) == "after")
                {
                    afterFound = true;
                    Assert.Equal(3, token.Line);
                }
            }

            Assert.True(macroFound);
            Assert.True(afterFound);
        }

        [Fact]
        public void IfZeroBlockIsSkipped()
        {
            const string code = "#if 0\nclass Ghost {};\n#endif\nclass Real {};";
            Assert.DoesNotContain(Tokenize(code), token =>
                token.Kind == CppTokenKind.Identifier && TextAt(code, token) == "Ghost");
            Assert.Contains(Tokenize(code), token =>
                token.Kind == CppTokenKind.Identifier && TextAt(code, token) == "Real");
        }

        [Fact]
        public void ElseBranchOfIfZeroIsIndexed()
        {
            const string code = "#if 0\nint hidden;\n#else\nint visible;\n#endif";
            Assert.DoesNotContain(Tokenize(code), token =>
                token.Kind == CppTokenKind.Identifier && TextAt(code, token) == "hidden");
            Assert.Contains(Tokenize(code), token =>
                token.Kind == CppTokenKind.Identifier && TextAt(code, token) == "visible");
        }

        [Fact]
        public void UnknownConditionalIndexesBothBranches()
        {
            const string code = "#ifdef FOO\nint a;\n#else\nint b;\n#endif";
            Assert.Contains(Tokenize(code), token =>
                token.Kind == CppTokenKind.Identifier && TextAt(code, token) == "a");
            Assert.Contains(Tokenize(code), token =>
                token.Kind == CppTokenKind.Identifier && TextAt(code, token) == "b");
        }

        [Fact]
        public void NestedIfZeroBlocksAreSkipped()
        {
            const string code = "#if 0\n#if 1\nint a;\n#endif\nint b;\n#endif\nint c;";
            Assert.DoesNotContain(Tokenize(code), token =>
                token.Kind == CppTokenKind.Identifier && TextAt(code, token) == "a");
            Assert.DoesNotContain(Tokenize(code), token =>
                token.Kind == CppTokenKind.Identifier && TextAt(code, token) == "b");
            Assert.Contains(Tokenize(code), token =>
                token.Kind == CppTokenKind.Identifier && TextAt(code, token) == "c");
        }

        [Fact]
        public void EmptyNullAndGarbageInputAreTolerated()
        {
            Assert.Single(Tokenize(string.Empty));
            Assert.Single(CppTokenizer.Tokenize(null).Tokens);
            Assert.NotEmpty(Tokenize("\0\u0001{{{ ))) &&&"));
            Assert.NotEmpty(Tokenize(new string('{', 5000)));
        }
    }
}
