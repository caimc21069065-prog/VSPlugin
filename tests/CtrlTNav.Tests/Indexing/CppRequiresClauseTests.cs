using System.Collections.Generic;
using CtrlTNav.Core.Model;
using CtrlTNav.Indexing;
using Xunit;

namespace CtrlTNav.Tests.Indexing
{
    /// <summary>
    /// C++20 requires-clauses must not hide the declaration they constrain. The extractor skips the
    /// constraint expression and keeps parsing the class/function that follows it.
    /// </summary>
    public sealed class CppRequiresClauseTests
    {
        private static IReadOnlyList<NavigationItem> Extract(string content)
        {
            return new CppSymbolExtractor().Extract("C:\\src\\constrained.h", content);
        }

        [Fact]
        public void ClassWithLeadingRequiresClauseIsExtractedWithItsMembers()
        {
            const string code = """
                template <typename T>
                requires std::integral<T>
                class Foo
                {
                public:
                    T Value();
                };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("class ", "Foo", ItemKind.Class, null, ItemFlags.Template),
                new ExpectedSymbol("T ", "Value", ItemKind.Method, "Foo"));

            Assert.Null(IndexingTestData.Find(items, "integral"));
            Assert.Null(IndexingTestData.Find(items, "Foo", ItemKind.GlobalVariable));
        }

        [Fact]
        public void ClassWithTrailingRequiresClauseIsExtractedWithItsMembers()
        {
            const string code = """
                template <typename T>
                class Foo requires std::integral<T>
                {
                public:
                    T Value();
                };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("class ", "Foo", ItemKind.Class, null, ItemFlags.Template),
                new ExpectedSymbol("T ", "Value", ItemKind.Method, "Foo"));

            NavigationItem foo = IndexingTestData.Require(items, "Foo", ItemKind.Class);
            Assert.Equal("Foo", foo.Name);
            Assert.Equal("Foo", foo.FullName);
        }

        [Fact]
        public void FunctionWithRequiresExpressionIsExtracted()
        {
            const string code = """
                template <typename T>
                requires requires(T t) { t + t; }
                T Add(T a, T b);
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("T ", "Add", ItemKind.Function, null, ItemFlags.Template));
        }

        [Fact]
        public void FunctionWithDisjunctionConstraintIsExtracted()
        {
            const string code = """
                template <typename T>
                requires std::integral<T> || std::floating_point<T>
                T Add(T a, T b);
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("T ", "Add", ItemKind.Function, null, ItemFlags.Template));
        }

        [Fact]
        public void ParenthesisedConstraintAndClassVirtSpecifierAreExtracted()
        {
            const string code = """
                template <typename T>
                requires (sizeof(T) >= 4)
                struct Aligned final
                {
                };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("struct ", "Aligned", ItemKind.Struct, null, ItemFlags.Template));
        }

        [Fact]
        public void ConstrainedMemberFunctionTemplateIsExtracted()
        {
            const string code = """
                struct Sink
                {
                    template <typename T>
                    requires std::integral<T>
                    void Put(T value);
                };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("struct ", "Sink", ItemKind.Struct),
                new ExpectedSymbol("void ", "Put", ItemKind.Method, "Sink", ItemFlags.Template));
        }
    }
}
