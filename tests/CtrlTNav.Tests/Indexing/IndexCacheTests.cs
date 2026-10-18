using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CtrlTNav.Core.Model;
using CtrlTNav.Indexing;
using Xunit;

namespace CtrlTNav.Tests.Indexing
{
    public sealed class IndexCacheTests
    {
        private static NavigationItem SampleItem(string name)
        {
            return new NavigationItem(name, ItemKind.Class, "C:\\src\\cached.h", 12, 7)
            {
                FullName = "demo::" + name,
                ProjectName = "Demo",
                ProjectPath = "C:\\src\\Demo.vcxproj",
                Flags = ItemFlags.Public | ItemFlags.HeaderFile | ItemFlags.Definition,
                ContainerName = "demo",
                Signature = string.Empty,
                Language = "cpp",
            };
        }

        [Fact]
        public void SaveAndLoadRoundTripsEntriesAndItems()
        {
            using var temp = new TempDirectory();
            string cachePath = temp.Combine("index-cache.bin");
            DateTime timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var entry = new IndexCacheEntry
            {
                FilePath = "C:\\src\\cached.h",
                LastWriteUtc = timestamp,
                Length = 1234,
                Items = new[] { SampleItem("First"), SampleItem("Second") },
            };

            var cache = new IndexCache();
            cache.Save(cachePath, "C:\\src\\Demo.sln", new[] { entry });
            Assert.True(File.Exists(cachePath));

            IndexCache? loaded = IndexCache.Load(cachePath);
            Assert.NotNull(loaded);
            Assert.Equal("C:\\src\\Demo.sln", loaded!.SolutionPath);
            Assert.Single(loaded.Entries);

            IndexCacheEntry loadedEntry = loaded.Entries[0];
            Assert.Equal("C:\\src\\cached.h", loadedEntry.FilePath);
            Assert.Equal(timestamp, loadedEntry.LastWriteUtc);
            Assert.Equal(1234, loadedEntry.Length);
            Assert.Equal(2, loadedEntry.Items.Count);

            NavigationItem first = loadedEntry.Items[0];
            Assert.Equal("First", first.Name);
            Assert.Equal(ItemKind.Class, first.Kind);
            Assert.Equal(12, first.Line);
            Assert.Equal(7, first.Column);
            Assert.Equal("demo::First", first.FullName);
            Assert.Equal("Demo", first.ProjectName);
            Assert.Equal("C:\\src\\Demo.vcxproj", first.ProjectPath);
            Assert.Equal(ItemFlags.Public | ItemFlags.HeaderFile | ItemFlags.Definition, first.Flags);
            Assert.Equal("demo", first.ContainerName);
            Assert.Equal("cpp", first.Language);
            Assert.Equal("C:\\src\\cached.h", first.FilePath);
            Assert.Equal(ItemKind.Class, loadedEntry.Items[1].Kind);
        }

        [Fact]
        public void SaveAndLoadEmptyCache()
        {
            using var temp = new TempDirectory();
            string cachePath = temp.Combine("empty.bin");
            new IndexCache().Save(cachePath, "C:\\src\\Empty.sln", Array.Empty<IndexCacheEntry>());

            IndexCache? loaded = IndexCache.Load(cachePath);
            Assert.NotNull(loaded);
            Assert.Empty(loaded!.Entries);
            Assert.Equal("C:\\src\\Empty.sln", loaded.SolutionPath);
        }

        [Fact]
        public void LoadReturnsNullForMissingFile()
        {
            using var temp = new TempDirectory();
            Assert.Null(IndexCache.Load(temp.Combine("missing.bin")));
            Assert.Null(IndexCache.Load(string.Empty));
            Assert.Null(IndexCache.Load(null!));
        }

        [Fact]
        public void LoadReturnsNullForCorruptFiles()
        {
            using var temp = new TempDirectory();
            string random = temp.Combine("random.bin");
            File.WriteAllBytes(random, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
            Assert.Null(IndexCache.Load(random));

            string text = temp.Combine("text.bin");
            File.WriteAllText(text, "this is definitely not an index cache");
            Assert.Null(IndexCache.Load(text));

            string magicOnly = temp.Combine("magic.bin");
            File.WriteAllBytes(magicOnly, Encoding.ASCII.GetBytes("CTNIDX1\0"));
            Assert.Null(IndexCache.Load(magicOnly));

            string emptyFile = temp.Combine("empty.bin");
            File.WriteAllBytes(emptyFile, Array.Empty<byte>());
            Assert.Null(IndexCache.Load(emptyFile));
        }

        [Fact]
        public void LoadReturnsNullForTruncatedCache()
        {
            using var temp = new TempDirectory();
            string cachePath = temp.Combine("truncated.bin");
            var entry = new IndexCacheEntry
            {
                FilePath = "C:\\src\\a.h",
                LastWriteUtc = DateTime.UtcNow,
                Length = 10,
                Items = new[] { SampleItem("A"), SampleItem("B") },
            };
            new IndexCache().Save(cachePath, "C:\\src\\A.sln", new[] { entry });

            byte[] bytes = File.ReadAllBytes(cachePath);
            byte[] truncated = new byte[bytes.Length / 2];
            Array.Copy(bytes, truncated, truncated.Length);
            File.WriteAllBytes(cachePath, truncated);

            Assert.Null(IndexCache.Load(cachePath));
        }

        [Fact]
        public void LoadReturnsNullForWrongMagicOrVersion()
        {
            using var temp = new TempDirectory();
            string wrongMagic = temp.Combine("wrong-magic.bin");
            var bytes = new List<byte>(Encoding.ASCII.GetBytes("NOTCTN1\0"));
            bytes.AddRange(BitConverter.GetBytes(1));
            File.WriteAllBytes(wrongMagic, bytes.ToArray());
            Assert.Null(IndexCache.Load(wrongMagic));

            string wrongVersion = temp.Combine("wrong-version.bin");
            bytes = new List<byte>(Encoding.ASCII.GetBytes("CTNIDX1\0"));
            bytes.AddRange(BitConverter.GetBytes(999));
            File.WriteAllBytes(wrongVersion, bytes.ToArray());
            Assert.Null(IndexCache.Load(wrongVersion));
        }

        [Fact]
        public void IsUpToDateDetectsChangedFiles()
        {
            using var temp = new TempDirectory();
            string filePath = temp.Write("tracked.h", "class Tracked {};\n");
            var fileInfo = new FileInfo(filePath);
            var entry = new IndexCacheEntry
            {
                FilePath = filePath,
                Length = fileInfo.Length,
                LastWriteUtc = fileInfo.LastWriteTimeUtc,
            };

            Assert.True(IndexCache.IsUpToDate(entry));

            File.AppendAllText(filePath, "// changed\n");
            Assert.False(IndexCache.IsUpToDate(entry));

            Assert.False(IndexCache.IsUpToDate(new IndexCacheEntry
            {
                FilePath = temp.Combine("missing.h"),
                Length = 1,
                LastWriteUtc = DateTime.UtcNow,
            }));

            Assert.False(IndexCache.IsUpToDate(null!));
        }

        [Fact]
        public void SaveCreatesDirectoryAndSwallowsInvalidPaths()
        {
            using var temp = new TempDirectory();
            string cachePath = temp.Combine(Path.Combine("nested", "deeper", "cache.bin"));
            new IndexCache().Save(cachePath, "C:\\src\\A.sln", Array.Empty<IndexCacheEntry>());
            Assert.True(File.Exists(cachePath));

            // Invalid characters / null path must not throw.
            new IndexCache().Save(null!, "x", Array.Empty<IndexCacheEntry>());
            new IndexCache().Save(string.Empty, "x", Array.Empty<IndexCacheEntry>());
            new IndexCache().Save(cachePath, "x", null!);
        }

        [Fact]
        public void CachedItemsFromExtractorSurviveRoundTrip()
        {
            using var temp = new TempDirectory();
            string sourcePath = temp.Write("cached.h", "namespace demo { class Cached { public: void Run(); }; }\n");
            string content = File.ReadAllText(sourcePath);
            IReadOnlyList<NavigationItem> items =
                new CppSymbolExtractor().Extract(sourcePath, content, "Demo", "Demo.vcxproj", "cpp");

            var entry = new IndexCacheEntry
            {
                FilePath = sourcePath,
                LastWriteUtc = File.GetLastWriteTimeUtc(sourcePath),
                Length = new FileInfo(sourcePath).Length,
                Items = items,
            };

            string cachePath = temp.Combine("extracted.bin");
            new IndexCache().Save(cachePath, "C:\\src\\Demo.sln", new[] { entry });
            IndexCache? loaded = IndexCache.Load(cachePath);

            Assert.NotNull(loaded);
            Assert.Equal(items.Count, loaded!.Entries[0].Items.Count);
            for (int index = 0; index < items.Count; index++)
            {
                Assert.Equal(items[index].Name, loaded.Entries[0].Items[index].Name);
                Assert.Equal(items[index].Kind, loaded.Entries[0].Items[index].Kind);
                Assert.Equal(items[index].Line, loaded.Entries[0].Items[index].Line);
                Assert.Equal(items[index].Column, loaded.Entries[0].Items[index].Column);
                Assert.Equal(items[index].FullName, loaded.Entries[0].Items[index].FullName);
                Assert.Equal(items[index].FilePath, loaded.Entries[0].Items[index].FilePath);
            }
        }
    }
}
