using System;
using System.Collections.Generic;
using CtrlTNav.Core.Indexing;
using CtrlTNav.Core.Model;
using CtrlTNav.Indexing;
using Xunit;

namespace CtrlTNav.Tests.Indexing
{
    public sealed class CppSymbolExtractorTests
    {
        private static IReadOnlyList<NavigationItem> Extract(string content, string filePath = "C:\\src\\test.cpp")
        {
            return new CppSymbolExtractor().Extract(filePath, content, "TestProject", "C:\\src\\Test.vcxproj", "cpp");
        }

        [Fact]
        public void ExtractsNamespacesNestedQualifiedAndAliases()
        {
            const string code = """
                namespace first
                {
                    namespace second
                    {
                        int DeepFunction();
                    }
                }
                namespace third::fourth
                {
                    class Nested {};
                }
                namespace alias_target = first::second;
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("namespace ", "first", ItemKind.Namespace),
                new ExpectedSymbol("namespace ", "second", ItemKind.Namespace, "first"),
                new ExpectedSymbol("int ", "DeepFunction", ItemKind.Function, "first::second"),
                new ExpectedSymbol("namespace third::", "fourth", ItemKind.Namespace),
                new ExpectedSymbol("class ", "Nested", ItemKind.Class, "third::fourth"),
                new ExpectedSymbol("namespace ", "alias_target", ItemKind.Namespace));

            NavigationItem fourth = IndexingTestData.Require(items, "fourth", ItemKind.Namespace);
            Assert.Equal("third::fourth", fourth.FullName);
            NavigationItem alias = IndexingTestData.Require(items, "alias_target", ItemKind.Namespace);
            Assert.True((alias.Flags & ItemFlags.Declaration) != 0);
        }

        [Fact]
        public void AnonymousNamespaceMembersKeepEnclosingContainer()
        {
            const string code = """
                namespace outer
                {
                    namespace
                    {
                        int HiddenHelper();
                    }
                }
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            Assert.Equal(2, items.Count);
            IndexingTestData.AssertSymbol(items, code, "int ", "HiddenHelper", ItemKind.Function, "outer");
        }

        [Fact]
        public void ExtractsClassStructUnionInterfaceAndForwardDeclarations()
        {
            const string code = """
                class Alpha;
                struct Beta {};
                union Gamma
                {
                    int i;
                    float f;
                };
                __interface IService
                {
                    void Run();
                };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("class ", "Alpha", ItemKind.Class),
                new ExpectedSymbol("struct ", "Beta", ItemKind.Struct),
                new ExpectedSymbol("union ", "Gamma", ItemKind.Union),
                new ExpectedSymbol("int ", "i", ItemKind.Field, "Gamma"),
                new ExpectedSymbol("float ", "f", ItemKind.Field, "Gamma"),
                new ExpectedSymbol("__interface ", "IService", ItemKind.Interface),
                new ExpectedSymbol("void ", "Run", ItemKind.Method, "IService"));

            NavigationItem alpha = IndexingTestData.Require(items, "Alpha", ItemKind.Class);
            Assert.True((alpha.Flags & ItemFlags.Declaration) != 0);
            NavigationItem beta = IndexingTestData.Require(items, "Beta", ItemKind.Struct);
            Assert.True((beta.Flags & ItemFlags.Definition) != 0);
        }

        [Fact]
        public void ExtractsEnumsEnumClassesMembersAndForwardEnums()
        {
            const string code = """
                enum class Scoped : int
                {
                    First,
                    Second = 2
                };
                enum Plain { PlainA, PlainB };
                enum { HiddenA, HiddenB };
                enum ForwardEnum : long;
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("enum class ", "Scoped", ItemKind.Enum),
                new ExpectedSymbol("", "First", ItemKind.EnumMember, "Scoped"),
                new ExpectedSymbol("", "Second", ItemKind.EnumMember, "Scoped"),
                new ExpectedSymbol("enum ", "Plain", ItemKind.Enum),
                new ExpectedSymbol("", "PlainA", ItemKind.EnumMember, "Plain"),
                new ExpectedSymbol("", "PlainB", ItemKind.EnumMember, "Plain"),
                new ExpectedSymbol("", "HiddenA", ItemKind.EnumMember),
                new ExpectedSymbol("", "HiddenB", ItemKind.EnumMember),
                new ExpectedSymbol("enum ", "ForwardEnum", ItemKind.Enum));

            NavigationItem forward = IndexingTestData.Require(items, "ForwardEnum", ItemKind.Enum);
            Assert.True((forward.Flags & ItemFlags.Declaration) != 0);
        }

        [Fact]
        public void ExtractsTypedefsUsingAliasesAndStructTypedefs()
        {
            const string code = """
                typedef unsigned long Size;
                typedef void (*Callback)(int value);
                typedef struct Tag
                {
                    int member;
                } TagAlias;
                using StringAlias = std::string;
                template <typename T>
                using VectorAlias = std::vector<T>;
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("typedef unsigned long ", "Size", ItemKind.TypeAlias),
                new ExpectedSymbol("(*", "Callback", ItemKind.TypeAlias),
                new ExpectedSymbol("struct ", "Tag", ItemKind.Struct),
                new ExpectedSymbol("int ", "member", ItemKind.Field, "Tag"),
                new ExpectedSymbol("} ", "TagAlias", ItemKind.TypeAlias),
                new ExpectedSymbol("using ", "StringAlias", ItemKind.TypeAlias),
                new ExpectedSymbol("using ", "VectorAlias", ItemKind.TypeAlias, null, ItemFlags.Template));
        }

        [Fact]
        public void ExtractsConcepts()
        {
            const string code = """
                template <typename T>
                concept Addable = requires(T a, T b)
                {
                    a + b;
                };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("concept ", "Addable", ItemKind.Concept, null, ItemFlags.Template));

            NavigationItem concept = IndexingTestData.Require(items, "Addable", ItemKind.Concept);
            Assert.True((concept.Flags & ItemFlags.Declaration) != 0);
        }

        [Fact]
        public void ExtractsFreeFunctionsWithDeclarationAndDefinitionFlags()
        {
            const string code = """
                int Add(int left, int right);
                inline double Scale(double value);
                static void Internal();
                extern "C" int CFunction(int value);
                void Definition(int value)
                {
                    int local = value;
                }
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("int ", "Add", ItemKind.Function),
                new ExpectedSymbol("double ", "Scale", ItemKind.Function),
                new ExpectedSymbol("void ", "Internal", ItemKind.Function, null, ItemFlags.Static),
                new ExpectedSymbol("int ", "CFunction", ItemKind.Function),
                new ExpectedSymbol("void ", "Definition", ItemKind.Function, null, ItemFlags.Definition));

            Assert.Null(IndexingTestData.Find(items, "local"));
            NavigationItem add = IndexingTestData.Require(items, "Add", ItemKind.Function);
            Assert.True((add.Flags & ItemFlags.Declaration) != 0);
            Assert.True((add.Flags & ItemFlags.NamespaceScope) != 0);
            Assert.Equal("(int left, int right)", add.Signature);
        }

        [Fact]
        public void ExtractsMethodsConstructorsDestructorsAndOperators()
        {
            const string code = """
                class Gadget
                {
                public:
                    Gadget();
                    Gadget(int size);
                    ~Gadget();
                    int GetSize() const;
                    void SetSize(int size);
                    Gadget& operator=(const Gadget& other);
                    bool operator==(const Gadget& other) const;
                    int operator()(int value) const;
                    int operator[](int index) const;
                    operator bool() const;
                    static Gadget Create();
                };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("class ", "Gadget", ItemKind.Class),
                new ExpectedSymbol("", "Gadget", ItemKind.Constructor, skipPositionCheck: true),
                new ExpectedSymbol("", "Gadget", ItemKind.Constructor, skipPositionCheck: true),
                new ExpectedSymbol("~", "Gadget", ItemKind.Destructor, "Gadget"),
                new ExpectedSymbol("int ", "GetSize", ItemKind.Method, "Gadget"),
                new ExpectedSymbol("void ", "SetSize", ItemKind.Method, "Gadget"),
                new ExpectedSymbol("", "operator=", ItemKind.Operator, "Gadget"),
                new ExpectedSymbol("", "operator==", ItemKind.Operator, "Gadget"),
                new ExpectedSymbol("", "operator()", ItemKind.Operator, "Gadget"),
                new ExpectedSymbol("", "operator[]", ItemKind.Operator, "Gadget"),
                new ExpectedSymbol("", "operator bool", ItemKind.Operator, "Gadget"),
                new ExpectedSymbol("Gadget ", "Create", ItemKind.Method, "Gadget", ItemFlags.Static));

            NavigationItem destructor = IndexingTestData.Require(items, "Gadget", ItemKind.Destructor);
            Assert.Equal("Gadget", destructor.Name);
            NavigationItem getSize = IndexingTestData.Require(items, "GetSize", ItemKind.Method);
            Assert.Equal("Gadget", getSize.ContainerName);
            Assert.Equal("Gadget::GetSize", getSize.FullName);
        }

        [Fact]
        public void ExtractsTemplates()
        {
            const string code = """
                template <typename T>
                class Holder
                {
                public:
                    T Get() const;
                private:
                    T value;
                };

                template <typename T>
                T Max(T left, T right);

                template <typename T>
                struct Traits
                {
                };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("class ", "Holder", ItemKind.Class, null, ItemFlags.Template),
                new ExpectedSymbol("T ", "Get", ItemKind.Method, "Holder"),
                new ExpectedSymbol("T ", "value", ItemKind.Field, "Holder"),
                new ExpectedSymbol("T ", "Max", ItemKind.Function, null, ItemFlags.Template),
                new ExpectedSymbol("struct ", "Traits", ItemKind.Struct, null, ItemFlags.Template));
        }

        [Fact]
        public void ExtractsTrailingReturnTypesQualifiersAndMultiLineSignatures()
        {
            const string code = """
                auto Compute() -> int;
                auto Snapshot() const noexcept -> const Widget&;
                void MultiLine(
                    int first,
                    double second = 2.5,
                    const char* third = nullptr)
                    noexcept;
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("auto ", "Compute", ItemKind.Function),
                new ExpectedSymbol("auto ", "Snapshot", ItemKind.Function),
                new ExpectedSymbol("void ", "MultiLine", ItemKind.Function));

            NavigationItem multiLine = IndexingTestData.Require(items, "MultiLine", ItemKind.Function);
            Assert.Contains("int first", multiLine.Signature);
            Assert.Contains("double second = 2.5", multiLine.Signature);
            Assert.DoesNotContain("\n", multiLine.Signature);
            Assert.DoesNotContain("\r", multiLine.Signature);
        }

        [Fact]
        public void ExtractsGlobalVariablesAndFieldsWithAccessFlags()
        {
            const string code = """
                int gCounter;
                static double gScale = 1.5;
                extern int gExternal;
                int gArray[4];
                char* gName = nullptr;

                class Settings
                {
                public:
                    int Visible;
                protected:
                    int ProtectedValue;
                private:
                    int Hidden;
                    static int s_InstanceCount;
                };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("int ", "gCounter", ItemKind.GlobalVariable, null, ItemFlags.Definition),
                new ExpectedSymbol("double ", "gScale", ItemKind.GlobalVariable, null, ItemFlags.Static),
                new ExpectedSymbol("int ", "gExternal", ItemKind.GlobalVariable, null, ItemFlags.Declaration),
                new ExpectedSymbol("int ", "gArray", ItemKind.GlobalVariable),
                new ExpectedSymbol("char* ", "gName", ItemKind.GlobalVariable),
                new ExpectedSymbol("class ", "Settings", ItemKind.Class),
                new ExpectedSymbol("int ", "Visible", ItemKind.Field, "Settings", ItemFlags.Public),
                new ExpectedSymbol("int ", "ProtectedValue", ItemKind.Field, "Settings", ItemFlags.Protected),
                new ExpectedSymbol("int ", "Hidden", ItemKind.Field, "Settings", ItemFlags.Private),
                new ExpectedSymbol("int ", "s_InstanceCount", ItemKind.Field, "Settings",
                    ItemFlags.Private | ItemFlags.Static));
        }

        [Fact]
        public void ExtractsFunctionPointerDeclarations()
        {
            const string code = """
                void (*gCallback)(int, int);
                typedef void (*Handler)(int);
                class Worker
                {
                    void (*m_Handler)(int);
                };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("(*", "gCallback", ItemKind.GlobalVariable),
                new ExpectedSymbol("(*", "Handler", ItemKind.TypeAlias),
                new ExpectedSymbol("class ", "Worker", ItemKind.Class),
                new ExpectedSymbol("(*", "m_Handler", ItemKind.Field, "Worker", ItemFlags.Private));
        }

        [Fact]
        public void ExtractsObjectLikeAndFunctionLikeMacros()
        {
            const string code = """
                #define VERSION 3
                #define SQUARE(x) ((x) * (x))
                #define MULTI_LINE(x) \
                    ((x) + \
                     1)
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("#define ", "VERSION", ItemKind.Macro),
                new ExpectedSymbol("#define ", "SQUARE", ItemKind.Macro),
                new ExpectedSymbol("#define ", "MULTI_LINE", ItemKind.Macro));

            Assert.Equal(string.Empty, IndexingTestData.Require(items, "VERSION", ItemKind.Macro).Signature);
            Assert.Equal("(x)", IndexingTestData.Require(items, "SQUARE", ItemKind.Macro).Signature);
            Assert.Equal("(x)", IndexingTestData.Require(items, "MULTI_LINE", ItemKind.Macro).Signature);
            NavigationItem macro = IndexingTestData.Require(items, "SQUARE", ItemKind.Macro);
            Assert.True((macro.Flags & ItemFlags.NamespaceScope) != 0);
        }

        [Fact]
        public void ExtractsExternCAndIgnoresIncludes()
        {
            const string code = """
                #include <string>
                extern "C"
                {
                    int CFunc(int value);
                }
                extern "C" int CInline(int value);
                struct AfterExtern
                {
                    int field;
                };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("int ", "CFunc", ItemKind.Function),
                new ExpectedSymbol("int ", "CInline", ItemKind.Function),
                new ExpectedSymbol("struct ", "AfterExtern", ItemKind.Struct),
                new ExpectedSymbol("int ", "field", ItemKind.Field, "AfterExtern", ItemFlags.Public));
        }

        [Fact]
        public void ExtractsNestedTypesWithQualifiedContainers()
        {
            const string code = """
                namespace ns
                {
                    class Outer
                    {
                    public:
                        class Inner
                        {
                        public:
                            void Method();
                        };
                        struct Nested
                        {
                            int value;
                        };
                    };
                }
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("namespace ", "ns", ItemKind.Namespace),
                new ExpectedSymbol("class ", "Outer", ItemKind.Class, "ns"),
                new ExpectedSymbol("class ", "Inner", ItemKind.Class, "ns::Outer", ItemFlags.Public),
                new ExpectedSymbol("void ", "Method", ItemKind.Method, "ns::Outer::Inner", ItemFlags.Public),
                new ExpectedSymbol("struct ", "Nested", ItemKind.Struct, "ns::Outer", ItemFlags.Public),
                new ExpectedSymbol("int ", "value", ItemKind.Field, "ns::Outer::Nested", ItemFlags.Public));

            NavigationItem method = IndexingTestData.Require(items, "Method", ItemKind.Method);
            Assert.Equal("ns::Outer::Inner::Method", method.FullName);
            Assert.True((method.Flags & ItemFlags.ClassScope) != 0);
        }

        [Fact]
        public void ExtractsOutOfClassMemberDefinitions()
        {
            const string code = """
                namespace ns
                {
                    class Engine
                    {
                    public:
                        void Start();
                        int operator-() const;
                    };
                    void Engine::Start()
                    {
                    }
                    int Engine::operator-() const
                    {
                        return 0;
                    }
                }
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("namespace ", "ns", ItemKind.Namespace),
                new ExpectedSymbol("class ", "Engine", ItemKind.Class, "ns"),
                new ExpectedSymbol("void ", "Start", ItemKind.Method, "ns::Engine"),
                new ExpectedSymbol("int ", "operator-", ItemKind.Operator, "ns::Engine"),
                new ExpectedSymbol("void Engine::", "Start", ItemKind.Method, "ns::Engine", ItemFlags.Definition),
                new ExpectedSymbol("int Engine::", "operator-", ItemKind.Operator, "ns::Engine", ItemFlags.Definition));
        }

        [Fact]
        public void IgnoresFunctionBodyLocalsAndInnerIdentifiers()
        {
            const string code = """
                int Before = 1;
                void Function()
                {
                    int localVar = 0;
                    struct LocalType { int localField; };
                    auto lambda = []() { return 1; };
                    if (localVar) { localVar++; }
                }
                int After = 2;
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("int ", "Before", ItemKind.GlobalVariable),
                new ExpectedSymbol("void ", "Function", ItemKind.Function),
                new ExpectedSymbol("int ", "After", ItemKind.GlobalVariable));

            Assert.Null(IndexingTestData.Find(items, "localVar"));
            Assert.Null(IndexingTestData.Find(items, "localField"));
            Assert.Null(IndexingTestData.Find(items, "LocalType"));
            Assert.Null(IndexingTestData.Find(items, "lambda"));
        }

        [Fact]
        public void IfZeroBlocksAreIgnoredButElseBranchesAreIndexed()
        {
            const string code = """
                #if 0
                class Hidden {};
                #else
                class Visible {};
                #endif
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            Assert.Single(items);
            IndexingTestData.AssertSymbol(items, code, "class ", "Visible", ItemKind.Class);
            Assert.Null(IndexingTestData.Find(items, "Hidden"));
        }

        [Fact]
        public void SkipsMacroInvocationsAndStaticAssertions()
        {
            const string code = """
                DECLARE_THING(Foo)
                SOME_MACRO(1, 2)
                Q_OBJECT
                assert(1);
                static_assert(sizeof(int) > 0, "message");
                class Real { };
                int keep = 1;
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("class ", "Real", ItemKind.Class),
                new ExpectedSymbol("int ", "keep", ItemKind.GlobalVariable));
        }

        [Fact]
        public void ExtractsVariablesWithMultipleDeclarators()
        {
            const string code = """
                int a = 1, b(2), *c = nullptr;
                static int d, e;
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("int ", "a", ItemKind.GlobalVariable),
                new ExpectedSymbol("", "b", ItemKind.GlobalVariable),
                new ExpectedSymbol("", "c", ItemKind.GlobalVariable),
                new ExpectedSymbol("", "d", ItemKind.GlobalVariable, null, ItemFlags.Static),
                new ExpectedSymbol("", "e", ItemKind.GlobalVariable, null, ItemFlags.Static));

            Assert.DoesNotContain(items, item => item.Kind == ItemKind.Function);
        }

        [Fact]
        public void ExtractsAnonymousUnionAndStructMembers()
        {
            const string code = """
                class Mixed
                {
                public:
                    union
                    {
                        int AsInteger;
                        float AsFloat;
                    };
                    struct
                    {
                        int X;
                        int Y;
                    } Point;
                };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("class ", "Mixed", ItemKind.Class),
                new ExpectedSymbol("int ", "AsInteger", ItemKind.Field, "Mixed"),
                new ExpectedSymbol("float ", "AsFloat", ItemKind.Field, "Mixed"),
                new ExpectedSymbol("int ", "X", ItemKind.Field, "Mixed::Point"),
                new ExpectedSymbol("int ", "Y", ItemKind.Field, "Mixed::Point"),
                new ExpectedSymbol("} ", "Point", ItemKind.Field, "Mixed"));
        }

        [Fact]
        public void SupportsAttributesAndExportModifiers()
        {
            const string code = """
                [[nodiscard]] int Attributed();
                __declspec(dllexport) int Exported();
                __attribute__((visibility("default"))) int VisibilityExported();
                class [[deprecated]] Old { };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("int ", "Attributed", ItemKind.Function),
                new ExpectedSymbol("int ", "Exported", ItemKind.Function, null, ItemFlags.Exported),
                new ExpectedSymbol("int ", "VisibilityExported", ItemKind.Function, null, ItemFlags.Exported),
                new ExpectedSymbol("class [[deprecated]] ", "Old", ItemKind.Class));
        }

        [Fact]
        public void SupportsQtStyleAccessLabels()
        {
            const string code = """
                class QtStyle
                {
                public slots:
                    void OnClicked();
                signals:
                    void Changed();
                private:
                    int m_Value;
                };
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertExactSymbols(
                items,
                code,
                new ExpectedSymbol("class ", "QtStyle", ItemKind.Class),
                new ExpectedSymbol("void ", "OnClicked", ItemKind.Method, "QtStyle", ItemFlags.Public),
                new ExpectedSymbol("void ", "Changed", ItemKind.Method, "QtStyle"),
                new ExpectedSymbol("int ", "m_Value", ItemKind.Field, "QtStyle", ItemFlags.Private));
        }

        [Fact]
        public void SetsHeaderFileFlagAndMetadata()
        {
            const string code = "class HeaderOnly { };";
            IReadOnlyList<NavigationItem> items = new CppSymbolExtractor().Extract(
                "C:\\src\\sample.h",
                code,
                "MyProject",
                "C:\\src\\MyProject.vcxproj",
                "c");

            NavigationItem item = IndexingTestData.Require(items, "HeaderOnly", ItemKind.Class);
            Assert.True((item.Flags & ItemFlags.HeaderFile) != 0);
            Assert.Equal("MyProject", item.ProjectName);
            Assert.Equal("C:\\src\\MyProject.vcxproj", item.ProjectPath);
            Assert.Equal("c", item.Language);
            Assert.Equal("C:\\src\\sample.h", item.FilePath);
            Assert.Equal("HeaderOnly", item.FullName);
            Assert.Equal(1, item.Line);
        }

        [Fact]
        public void SetsNamespaceAndClassScopeFlags()
        {
            const string code = """
                namespace ns
                {
                    int FreeFunction();

                    class C
                    {
                    public:
                        int Member;
                        void Method();
                    };
                }
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            NavigationItem function = IndexingTestData.Require(items, "FreeFunction", ItemKind.Function);
            Assert.True((function.Flags & ItemFlags.NamespaceScope) != 0);
            Assert.False((function.Flags & ItemFlags.ClassScope) != 0);

            NavigationItem field = IndexingTestData.Require(items, "Member", ItemKind.Field);
            Assert.True((field.Flags & ItemFlags.ClassScope) != 0);
            Assert.False((field.Flags & ItemFlags.NamespaceScope) != 0);

            NavigationItem method = IndexingTestData.Require(items, "Method", ItemKind.Method);
            Assert.True((method.Flags & ItemFlags.ClassScope) != 0);
        }

        [Fact]
        public void NeverThrowsOnMalformedOrTruncatedInput()
        {
            string[] inputs =
            {
                string.Empty,
                "} } ; ) ( [ ] . -> :: ~ ! ?",
                "class",
                "struct {",
                "void f(",
                "int x = ",
                "/* never closed",
                "\"unterminated",
                "R\"(raw never closed",
                "template<",
                "#if",
                "#if 0",
                "#define",
                "#define (",
                "class A { class",
                "namespace {",
                "typedef",
                "enum {",
                "operator",
                "std::vector<",
                "int a[] = {1,2",
                "const char* s = \"unterminated\nclass AfterString {};",
                "auto raw = R\"(unterminated\nclass AfterRaw {};",
                "int f() { return 1; } } } class Stray { };",
                new string('{', 10000),
                "int x = (((((((((((((((((",
                "union { int",
                "struct : public",
                "#if 0\n#if 0\n#endif",
                "class Truncated { void m("
            };

            var extractor = new CppSymbolExtractor();
            for (int index = 0; index < inputs.Length; index++)
            {
                IReadOnlyList<NavigationItem> items = extractor.Extract("broken.h", inputs[index], "P", "P.vcxproj", "cpp");
                Assert.NotNull(items);
            }
        }

        [Fact]
        public void UnterminatedStringDoesNotHideFollowingSymbols()
        {
            const string code = "class First {};\nconst char* s = \"unterminated\nclass Second {};";
            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertSymbol(items, code, "class ", "First", ItemKind.Class);
            IndexingTestData.AssertSymbol(items, code, "class ", "Second", ItemKind.Class);
        }

        [Fact]
        public void UnterminatedRawStringSwallowsRemainderWithoutThrowing()
        {
            const string code = "class First {};\nauto raw = R\"(unterminated\nclass Second {};";
            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertSymbol(items, code, "class ", "First", ItemKind.Class);
            Assert.Null(IndexingTestData.Find(items, "Second"));
        }

        [Fact]
        public void DoesNotThrowOnBinaryLikeContent()
        {
            var builder = new System.Text.StringBuilder();
            for (int index = 0; index < 500; index++)
            {
                builder.Append((char)(index % 256));
            }

            IReadOnlyList<NavigationItem> items = Extract(builder.ToString());
            Assert.NotNull(items);
        }

        [Fact]
        public void ExtractsEmptyFileAsNoSymbols()
        {
            IReadOnlyList<NavigationItem> items = Extract(string.Empty);
            Assert.Empty(items);

            IReadOnlyList<NavigationItem> commentOnly = Extract("// only a comment\n/* and a block */\n");
            Assert.Empty(commentOnly);
        }

        [Fact]
        public void ExtractsNamespaceDefinitionsFromMultipleSeparateBlocks()
        {
            const string code = """
                namespace alpha
                {
                    int One();
                }
                namespace beta
                {
                    int Two();
                }
                namespace alpha
                {
                    int Three();
                }
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertSymbol(items, code, "int ", "One", ItemKind.Function, "alpha");
            IndexingTestData.AssertSymbol(items, code, "int ", "Two", ItemKind.Function, "beta");
            IndexingTestData.AssertSymbol(items, code, "int ", "Three", ItemKind.Function, "alpha");
            Assert.Equal(6, items.Count);
        }

        [Fact]
        public void ExtractsQualifiedNamespaceAliasesAndUses()
        {
            const string code = """
                namespace a { namespace b { class Deep {}; } }
                using namespace a::b;
                using DeepAlias = a::b::Deep;
                """;

            IReadOnlyList<NavigationItem> items = Extract(code);
            IndexingTestData.AssertSymbol(items, code, "class ", "Deep", ItemKind.Class, "a::b");
            IndexingTestData.AssertSymbol(items, code, "using ", "DeepAlias", ItemKind.TypeAlias);
            Assert.Null(IndexingTestData.Find(items, "namespace"));
        }
    }
}
