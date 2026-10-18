using System;
using System.Collections.Generic;
using System.IO;
using CtrlTNav.Core.Indexing;
using CtrlTNav.Core.Options;
using CtrlTNav.Indexing;
using Xunit;

namespace CtrlTNav.Tests.Indexing
{
    public sealed class SolutionFileParserTests
    {
        [Fact]
        public void ParseSolutionReturnsCppFilesFromAllProjectsInOrder()
        {
            IReadOnlyList<SourceFileInfo> files =
                SolutionFileParser.ParseSolution(IndexingTestData.PathOf("SampleSolution.sln"));

            Assert.Equal(5, files.Count);
            IndexingTestData.AssertEndsWith("sample.cpp", files[0].FilePath);
            IndexingTestData.AssertEndsWith("sample.h", files[1].FilePath);
            IndexingTestData.AssertEndsWith("proj_one_extra.hpp", files[2].FilePath);
            IndexingTestData.AssertEndsWith("extra.inl", files[3].FilePath);
            IndexingTestData.AssertEndsWith("proj_two_header.h", files[4].FilePath);

            Assert.Equal("ProjOne", files[0].ProjectName);
            Assert.Equal("ProjOne", files[1].ProjectName);
            Assert.Equal("ProjOne", files[2].ProjectName);
            Assert.Equal("ProjOne", files[3].ProjectName);
            Assert.Equal("ProjTwo", files[4].ProjectName);

            for (int index = 0; index < files.Count; index++)
            {
                Assert.True(Path.IsPathRooted(files[index].FilePath), "Expected an absolute path: " + files[index].FilePath);
                Assert.True(files[index].ProjectPath.Length > 0);
                Assert.Equal("cpp", files[index].Language);
            }

            IndexingTestData.AssertEndsWith("ProjOne.vcxproj", files[0].ProjectPath);
            IndexingTestData.AssertEndsWith("ProjTwo.vcxproj", files[4].ProjectPath);

            // $(ProjectDir) must have been expanded relative to the project directory.
            Assert.Equal(
                Path.GetFullPath(Path.Combine(IndexingTestData.PathOf("proj_one"), "proj_one_extra.hpp")),
                files[2].FilePath);
        }

        [Fact]
        public void ParseSolutionSkipsSolutionFoldersManagedProjectsAndMissingProjects()
        {
            IReadOnlyList<SourceFileInfo> files =
                SolutionFileParser.ParseSolution(IndexingTestData.PathOf("SampleSolution.sln"));

            for (int index = 0; index < files.Count; index++)
            {
                IndexingTestData.AssertDoesNotContain("Managed.csproj", files[index].ProjectPath);
                IndexingTestData.AssertDoesNotContain("Missing.vcxproj", files[index].ProjectPath);
                IndexingTestData.AssertDoesNotContain("SolutionItems", files[index].ProjectPath);
            }
        }

        [Fact]
        public void ParseSolutionToleratesMissingOrBrokenSolution()
        {
            Assert.Empty(SolutionFileParser.ParseSolution(IndexingTestData.PathOf("does_not_exist.sln")));
            Assert.Empty(SolutionFileParser.ParseSolution(string.Empty));
            Assert.Empty(SolutionFileParser.ParseSolution(null!));
        }

        [Fact]
        public void ParseSolutionToleratesGarbageContent()
        {
            using var temp = new TempDirectory();
            string path = temp.Write("Broken.sln", "this is not a solution file at all\nProject(\"{garbage}\")\n");
            Assert.Empty(SolutionFileParser.ParseSolution(path));
        }

        [Fact]
        public void ParseProjectReturnsExpectedFilesAndProjectName()
        {
            string projectPath = IndexingTestData.PathOf("proj_one\\ProjOne.vcxproj");
            IReadOnlyList<SourceFileInfo> files = SolutionFileParser.ParseProject(projectPath, "CustomName");

            Assert.Equal(4, files.Count);
            IndexingTestData.AssertEndsWith("sample.cpp", files[0].FilePath);
            IndexingTestData.AssertEndsWith("sample.h", files[1].FilePath);
            IndexingTestData.AssertEndsWith("proj_one_extra.hpp", files[2].FilePath);
            IndexingTestData.AssertEndsWith("extra.inl", files[3].FilePath);

            for (int index = 0; index < files.Count; index++)
            {
                Assert.Equal("CustomName", files[index].ProjectName);
                Assert.True(Path.IsPathRooted(files[index].FilePath));
                Assert.Equal(Path.GetFullPath(projectPath), files[index].ProjectPath);
            }

            Assert.DoesNotContain(files, file => file.FilePath.EndsWith("notes.txt", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(files, file => file.FilePath.IndexOf("wildcard", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void ParseProjectHandlesFileWithoutXmlNamespaceAndWithBom()
        {
            string projectPath = IndexingTestData.PathOf("proj_two\\ProjTwo.vcxproj");
            IReadOnlyList<SourceFileInfo> files = SolutionFileParser.ParseProject(projectPath);

            Assert.Equal(3, files.Count);
            IndexingTestData.AssertEndsWith("sample.cpp", files[0].FilePath);
            IndexingTestData.AssertEndsWith("proj_two_header.h", files[1].FilePath);
            IndexingTestData.AssertEndsWith("sample.h", files[2].FilePath);
            Assert.Equal("ProjTwo", files[0].ProjectName);
        }

        [Fact]
        public void ParseProjectToleratesMissingAndMalformedProjects()
        {
            Assert.Empty(SolutionFileParser.ParseProject(IndexingTestData.PathOf("nope.vcxproj")));
            Assert.Empty(SolutionFileParser.ParseProject(string.Empty));

            using var temp = new TempDirectory();
            string malformed = temp.Write("Broken.vcxproj", "<Project><ItemGroup>");
            Assert.Empty(SolutionFileParser.ParseProject(malformed));

            string noItems = temp.Write("Empty.vcxproj", "<Project></Project>");
            Assert.Empty(SolutionFileParser.ParseProject(noItems));
        }

        [Fact]
        public void ParseProjectFiltersUnresolvableVariablesAndUnsupportedExtensions()
        {
            using var temp = new TempDirectory();
            string project = temp.Write("Mixed.vcxproj", """
                <Project>
                  <ItemGroup>
                    <ClCompile Include="$(UnknownVariable)\foo.cpp" />
                    <ClCompile Include="$(ProjectDir)real.cpp" />
                    <ClInclude Include="include\real.hpp" />
                    <None Include="readme.md" />
                    <None Include="settings.ini" />
                  </ItemGroup>
                </Project>
                """);

            IReadOnlyList<SourceFileInfo> files = SolutionFileParser.ParseProject(project, "Mixed");

            Assert.Equal(2, files.Count);
            IndexingTestData.AssertEndsWith(Path.Combine("real.cpp"), files[0].FilePath);
            Assert.True(Path.IsPathRooted(files[0].FilePath));
            IndexingTestData.AssertEndsWith(Path.Combine("include", "real.hpp"), files[1].FilePath);
            Assert.Equal("Mixed", files[0].ProjectName);
        }
    }
}
