using System;
using System.Collections.Generic;
using System.IO;
using CtrlTNav.Core.Options;
using CtrlTNav.Indexing;
using Xunit;

namespace CtrlTNav.Tests.Indexing
{
    public sealed class SourceFileScannerTests
    {
        [Fact]
        public void ScanDirectoryFindsCppFilesAndAppliesDefaultExcludes()
        {
            string root = IndexingTestData.PathOf("scanroot");
            IReadOnlyList<CtrlTNav.Core.Indexing.SourceFileInfo> files =
                SourceFileScanner.ScanDirectory(root, CtrlTNavOptions.CreateDefault());

            Assert.Equal(3, files.Count);
            IndexingTestData.AssertEndsWith("a.cpp", files[0].FilePath);
            IndexingTestData.AssertEndsWith(Path.Combine("sub", "b.hpp"), files[1].FilePath);
            IndexingTestData.AssertEndsWith(Path.Combine("sub", "deep", "c.cc"), files[2].FilePath);

            for (int index = 0; index < files.Count; index++)
            {
                Assert.True(Path.IsPathRooted(files[index].FilePath));
                Assert.Equal("cpp", files[index].Language);
            }

            Assert.DoesNotContain(files, file =>
                file.FilePath.IndexOf("generated.cpp", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.DoesNotContain(files, file =>
                file.FilePath.IndexOf("notes.txt", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.DoesNotContain(files, file =>
                file.FilePath.IndexOf(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void ScanDirectoryHonorsCustomExcludePatterns()
        {
            string root = IndexingTestData.PathOf("scanroot");
            var options = new CtrlTNavOptions { ExcludePatterns = "**/sub/**" };
            IReadOnlyList<CtrlTNav.Core.Indexing.SourceFileInfo> files = SourceFileScanner.ScanDirectory(root, options);

            Assert.Single(files);
            IndexingTestData.AssertEndsWith("a.cpp", files[0].FilePath);
        }

        [Fact]
        public void ScanDirectoryToleratesMissingRoot()
        {
            Assert.Empty(SourceFileScanner.ScanDirectory(IndexingTestData.PathOf("no_such_directory"), CtrlTNavOptions.CreateDefault()));
            Assert.Empty(SourceFileScanner.ScanDirectory(string.Empty, CtrlTNavOptions.CreateDefault()));
            Assert.Empty(SourceFileScanner.ScanDirectory(null!, null!));
        }

        [Fact]
        public void ScanDirectoryReportsCLanguage()
        {
            using var temp = new TempDirectory();
            temp.Write("plain.c", "int legacy_function(void);\n");
            temp.Write("ignored.txt", "not c\n");

            IReadOnlyList<CtrlTNav.Core.Indexing.SourceFileInfo> files =
                SourceFileScanner.ScanDirectory(temp.Path, CtrlTNavOptions.CreateDefault());

            Assert.Single(files);
            IndexingTestData.AssertEndsWith("plain.c", files[0].FilePath);
            Assert.Equal("c", files[0].Language);
        }

        [Fact]
        public void IsSupportedFileChecksExtensionAndExcludes()
        {
            var options = CtrlTNavOptions.CreateDefault();
            Assert.True(SourceFileScanner.IsSupportedFile("C:\\src\\file.cpp", options));
            Assert.True(SourceFileScanner.IsSupportedFile("C:\\src\\file.hpp", options));
            Assert.True(SourceFileScanner.IsSupportedFile("C:\\src\\file.C", options));
            Assert.False(SourceFileScanner.IsSupportedFile("C:\\src\\file.txt", options));
            Assert.False(SourceFileScanner.IsSupportedFile("C:\\src\\build\\file.cpp", options));
            Assert.False(SourceFileScanner.IsSupportedFile("C:\\src\\.git\\file.cpp", options));
            Assert.False(SourceFileScanner.IsSupportedFile(string.Empty, options));
            Assert.False(SourceFileScanner.IsSupportedFile(null!, options));

            var headerOnly = new CtrlTNavOptions { SourceExtensions = ".hpp" };
            Assert.True(SourceFileScanner.IsSupportedFile("C:\\src\\file.hpp", headerOnly));
            Assert.False(SourceFileScanner.IsSupportedFile("C:\\src\\file.cpp", headerOnly));
        }
    }
}
