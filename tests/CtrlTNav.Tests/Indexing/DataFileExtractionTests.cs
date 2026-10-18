using System.Collections.Generic;
using CtrlTNav.Core.Model;
using Xunit;

namespace CtrlTNav.Tests.Indexing
{
    /// <summary>
    /// End-to-end extraction over realistic test-data files. The expected symbol lists are asserted
    /// exactly (kind + name + line + column), not just by count.
    /// </summary>
    public sealed class DataFileExtractionTests
    {
        [Fact]
        public void SampleHeaderExtractsExactlyTheExpectedSymbols()
        {
            const string relative = "sample.h";
            string content = IndexingTestData.Read(relative);
            IReadOnlyList<NavigationItem> items = IndexingTestData.Extract(relative);

            IndexingTestData.AssertExactSymbols(
                items,
                content,
                new ExpectedSymbol("#define ", "NAV_SAMPLE_VERSION", ItemKind.Macro),
                new ExpectedSymbol("#define ", "NAV_SAMPLE_MAX", ItemKind.Macro),
                new ExpectedSymbol("#define ", "NAV_SAMPLE_API", ItemKind.Macro),
                new ExpectedSymbol("#define ", "NAV_INNER_MACRO", ItemKind.Macro),
                new ExpectedSymbol("namespace ", "navsample", ItemKind.Namespace),
                new ExpectedSymbol("namespace ", "detail", ItemKind.Namespace, "navsample"),
                new ExpectedSymbol("enum class ", "Color", ItemKind.Enum, "navsample"),
                new ExpectedSymbol("", "Red", ItemKind.EnumMember, "navsample::Color"),
                new ExpectedSymbol("", "Green", ItemKind.EnumMember, "navsample::Color"),
                new ExpectedSymbol("", "Blue", ItemKind.EnumMember, "navsample::Color"),
                new ExpectedSymbol("enum ", "LegacyMode", ItemKind.Enum, "navsample"),
                new ExpectedSymbol("", "ModeOff", ItemKind.EnumMember, "navsample::LegacyMode"),
                new ExpectedSymbol("", "ModeOn", ItemKind.EnumMember, "navsample::LegacyMode"),
                new ExpectedSymbol("typedef unsigned long ", "SampleSize", ItemKind.TypeAlias, "navsample"),
                new ExpectedSymbol("(*", "SampleCallback", ItemKind.TypeAlias, "navsample"),
                new ExpectedSymbol("using ", "SampleAlias", ItemKind.TypeAlias, "navsample"),
                new ExpectedSymbol("using ", "SampleVector", ItemKind.TypeAlias, "navsample", ItemFlags.Template),
                new ExpectedSymbol("concept ", "Numeric", ItemKind.Concept, "navsample", ItemFlags.Template),
                new ExpectedSymbol("struct ", "Point", ItemKind.Struct, "navsample"),
                new ExpectedSymbol("int ", "X", ItemKind.Field, "navsample::Point"),
                new ExpectedSymbol("int ", "Y", ItemKind.Field, "navsample::Point"),
                new ExpectedSymbol("int ", "Dimensions", ItemKind.Field, "navsample::Point", ItemFlags.Static),
                new ExpectedSymbol("int ", "LengthSquared", ItemKind.Method, "navsample::Point"),
                new ExpectedSymbol("Point ", "Origin", ItemKind.Method, "navsample::Point", ItemFlags.Static),
                new ExpectedSymbol("", "Point", ItemKind.Constructor, "navsample::Point", skipPositionCheck: true),
                new ExpectedSymbol("~", "Point", ItemKind.Destructor, "navsample::Point"),
                new ExpectedSymbol("", "operator==", ItemKind.Operator, "navsample::Point"),
                new ExpectedSymbol("", "operator+", ItemKind.Operator, "navsample::Point"),
                new ExpectedSymbol("class NAV_SAMPLE_API ", "Widget", ItemKind.Class, "navsample"),
                new ExpectedSymbol("", "Widget", ItemKind.Constructor, "navsample::Widget", skipPositionCheck: true),
                new ExpectedSymbol("~", "Widget", ItemKind.Destructor, "navsample::Widget"),
                new ExpectedSymbol("void ", "Draw", ItemKind.Method, "navsample::Widget"),
                new ExpectedSymbol("int ", "ComputeWidth", ItemKind.Method, "navsample::Widget"),
                new ExpectedSymbol("Widget* ", "CreateDefault", ItemKind.Method, "navsample::Widget", ItemFlags.Static),
                new ExpectedSymbol("int ", "Width", ItemKind.Field, "navsample::Widget", ItemFlags.Protected),
                new ExpectedSymbol("bool ", "Visible", ItemKind.Field, "navsample::Widget", ItemFlags.Private),
                new ExpectedSymbol("int ", "LastWidth", ItemKind.Field, "navsample::Widget", ItemFlags.Private),
                new ExpectedSymbol("union ", "Value", ItemKind.Union, "navsample"),
                new ExpectedSymbol("int ", "Integer", ItemKind.Field, "navsample::Value"),
                new ExpectedSymbol("float ", "Real", ItemKind.Field, "navsample::Value"),
                new ExpectedSymbol("struct ", "ForwardDeclared", ItemKind.Struct, "navsample", ItemFlags.Declaration),
                new ExpectedSymbol("} ", "Pair", ItemKind.TypeAlias, "navsample"),
                new ExpectedSymbol("int ", "A", ItemKind.Field, "navsample::Pair"),
                new ExpectedSymbol("int ", "B", ItemKind.Field, "navsample::Pair"),
                new ExpectedSymbol("class ", "Container", ItemKind.Class, "navsample", ItemFlags.Template),
                new ExpectedSymbol("T ", "Get", ItemKind.Method, "navsample::Container"),
                new ExpectedSymbol("void ", "Set", ItemKind.Method, "navsample::Container"),
                new ExpectedSymbol("T* ", "items", ItemKind.Field, "navsample::Container"),
                new ExpectedSymbol("int ", "count", ItemKind.Field, "navsample::Container"),
                new ExpectedSymbol("NAV_SAMPLE_API ", "SampleAdd", ItemKind.Function, "navsample"),
                new ExpectedSymbol("int ", "InternalHelper", ItemKind.Function, "navsample::detail"),
                new ExpectedSymbol("int ", "AnonymousHelper", ItemKind.Function, "navsample::detail"),
                new ExpectedSymbol("SampleSize ", "gTotalCount", ItemKind.GlobalVariable, "navsample", ItemFlags.Declaration),
                new ExpectedSymbol("double ", "gRatio", ItemKind.GlobalVariable, "navsample", ItemFlags.Static),
                new ExpectedSymbol("", "AnonymousFirst", ItemKind.EnumMember),
                new ExpectedSymbol("", "AnonymousSecond", ItemKind.EnumMember),
                new ExpectedSymbol("int ", "GlobalOutsideNamespace", ItemKind.Function),
                new ExpectedSymbol("int ", "gOutsideStatic", ItemKind.GlobalVariable, null, ItemFlags.Static),
                new ExpectedSymbol("typedef int ", "GlobalIntAlias", ItemKind.TypeAlias));

            Assert.Equal(59, items.Count);
        }

        [Fact]
        public void SampleSourceExtractsDefinitionsAndSkipsLocals()
        {
            const string relative = "sample.cpp";
            string content = IndexingTestData.Read(relative);
            IReadOnlyList<NavigationItem> items = IndexingTestData.Extract(relative);

            IndexingTestData.AssertExactSymbols(
                items,
                content,
                new ExpectedSymbol("#define ", "CPP_FILE_MACRO", ItemKind.Macro),
                new ExpectedSymbol("namespace ", "navsample", ItemKind.Namespace, skipPositionCheck: true),
                new ExpectedSymbol("int Point::", "LengthSquared", ItemKind.Method, "navsample::Point",
                    ItemFlags.Definition),
                new ExpectedSymbol("", "Point", ItemKind.Constructor, "navsample::Point", ItemFlags.Definition,
                    skipPositionCheck: true),
                new ExpectedSymbol("~", "Point", ItemKind.Destructor, "navsample::Point", ItemFlags.Definition),
                new ExpectedSymbol("Point Point::", "Origin", ItemKind.Method, "navsample::Point", ItemFlags.Definition),
                new ExpectedSymbol("", "operator==", ItemKind.Operator, "navsample::Point", ItemFlags.Definition),
                new ExpectedSymbol("", "operator+", ItemKind.Operator, "navsample::Point", ItemFlags.Definition),
                new ExpectedSymbol("int ", "SampleAdd", ItemKind.Function, "navsample", ItemFlags.Definition),
                new ExpectedSymbol("int detail::", "InternalHelper", ItemKind.Method, "navsample::detail"),
                new ExpectedSymbol("SampleSize ", "gTotalCount", ItemKind.GlobalVariable, "navsample",
                    ItemFlags.Definition),
                new ExpectedSymbol("int Widget::", "ComputeWidth", ItemKind.Method, "navsample::Widget"),
                new ExpectedSymbol("int ", "GlobalOutsideNamespace", ItemKind.Function, null, ItemFlags.Definition),
                new ExpectedSymbol("int ", "gOutsideStatic", ItemKind.GlobalVariable, null, ItemFlags.Definition),
                new ExpectedSymbol("int ", "CLinkageFunction", ItemKind.Function, null, ItemFlags.Definition));

            Assert.Equal(15, items.Count);
            Assert.Null(IndexingTestData.Find(items, "localVariable"));
            Assert.Null(IndexingTestData.Find(items, "localStatic"));
        }

        [Fact]
        public void BrokenFileStillExtractsValidSymbolsAndSkipsIfZero()
        {
            const string relative = "broken.cpp";
            string content = IndexingTestData.Read(relative);
            IReadOnlyList<NavigationItem> items = IndexingTestData.Extract(relative);

            IndexingTestData.AssertExactSymbols(
                items,
                content,
                new ExpectedSymbol("#define ", "BROKEN_MACRO", ItemKind.Macro),
                new ExpectedSymbol("class ", "Survives", ItemKind.Class),
                new ExpectedSymbol("void ", "Inside", ItemKind.Method, "Survives"),
                new ExpectedSymbol("int ", "value", ItemKind.Field, "Survives"),
                new ExpectedSymbol("int ", "ValidFunction", ItemKind.Function, null, ItemFlags.Definition),
                new ExpectedSymbol("namespace ", "broken_ns", ItemKind.Namespace),
                new ExpectedSymbol("struct ", "Fine", ItemKind.Struct, "broken_ns"),
                new ExpectedSymbol("int ", "okay", ItemKind.Field, "broken_ns::Fine"),
                new ExpectedSymbol("class ", "AfterIf0", ItemKind.Class),
                new ExpectedSymbol("void ", "Visible", ItemKind.Method, "AfterIf0"),
                new ExpectedSymbol("char* ", "escaped", ItemKind.GlobalVariable),
                new ExpectedSymbol("int ", "AfterBrokenString", ItemKind.GlobalVariable),
                new ExpectedSymbol("void ", "TruncatedFunction", ItemKind.Function));

            Assert.Equal(13, items.Count);
            Assert.Null(IndexingTestData.Find(items, "Ghost"));
            Assert.Null(IndexingTestData.Find(items, "NotIndexed"));
        }

        [Fact]
        public void MessyThirdPartyHeaderExtractsExpectedSymbols()
        {
            const string relative = "messy_thirdparty.h";
            string content = IndexingTestData.Read(relative);
            IReadOnlyList<NavigationItem> items = IndexingTestData.Extract(relative);

            IndexingTestData.AssertExactSymbols(
                items,
                content,
                new ExpectedSymbol("#define ", "MESSY_THIRD_PARTY_H", ItemKind.Macro),
                new ExpectedSymbol("#define ", "MESSY_EXPORT", ItemKind.Macro),
                new ExpectedSymbol("#define ", "MESSY_CALL", ItemKind.Macro),
                new ExpectedSymbol("#define ", "MESSY_BEGIN_NAMESPACE", ItemKind.Macro),
                new ExpectedSymbol("#define ", "MESSY_END_NAMESPACE", ItemKind.Macro),
                new ExpectedSymbol("struct ", "_MESSY_HANDLE", ItemKind.Struct),
                new ExpectedSymbol("", "MESSY_HANDLE", ItemKind.TypeAlias, skipPositionCheck: true),
                new ExpectedSymbol("", "MESSY_CALLBACK", ItemKind.TypeAlias),
                new ExpectedSymbol("int MESSY_CALL ", "MessyApiFunction", ItemKind.Function),
                new ExpectedSymbol("class MESSY_EXPORT ", "MessyWidget", ItemKind.Class),
                new ExpectedSymbol("", "MessyWidget", ItemKind.Constructor, "MessyWidget", skipPositionCheck: true),
                new ExpectedSymbol("~", "MessyWidget", ItemKind.Destructor, "MessyWidget"),
                new ExpectedSymbol("int ", "Compute", ItemKind.Method, "MessyWidget"),
                new ExpectedSymbol("", "operator bool", ItemKind.Operator, "MessyWidget"),
                new ExpectedSymbol("", "operator=", ItemKind.Operator, "MessyWidget"),
                new ExpectedSymbol("void ", "MultiLine", ItemKind.Method, "MessyWidget"),
                new ExpectedSymbol("int ", "s_instanceCount", ItemKind.Field, "MessyWidget",
                    ItemFlags.Private | ItemFlags.Static),
                new ExpectedSymbol("unsigned int ", "flags", ItemKind.Field, "MessyWidget", ItemFlags.Private),
                new ExpectedSymbol("} ", "anonymousMember", ItemKind.Field, "MessyWidget", ItemFlags.Private),
                new ExpectedSymbol("int ", "nestedX", ItemKind.Field, "MessyWidget::anonymousMember"),
                new ExpectedSymbol("int ", "nestedY", ItemKind.Field, "MessyWidget::anonymousMember"));

            Assert.Equal(21, items.Count);
        }

        [Fact]
        public void SampleHeaderItemsHaveProjectMetadataAndHeaderFlag()
        {
            IReadOnlyList<NavigationItem> items = IndexingTestData.Extract("sample.h");
            NavigationItem point = IndexingTestData.Require(items, "Point", ItemKind.Struct);
            Assert.True((point.Flags & ItemFlags.HeaderFile) != 0);
            Assert.Equal("TestProject", point.ProjectName);
            IndexingTestData.AssertEndsWith("TestProject.vcxproj", point.ProjectPath);
            IndexingTestData.AssertEndsWith("sample.h", point.FilePath);
            Assert.Equal("navsample::Point", point.FullName);
            Assert.Equal("navsample", point.ContainerName);
        }
    }
}
