using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CtrlTNav.Core.Indexing;
using CtrlTNav.Core.Model;
using CtrlTNav.Core.Options;
using CtrlTNav.Indexing;
using Xunit;

namespace CtrlTNav.Tests.Indexing
{
    public sealed class CppIndexTests
    {
        private static SourceFileInfo Source(string filePath)
        {
            return new SourceFileInfo(filePath);
        }

        [Fact]
        public async Task BuildAsyncIndexesFilesAndAddsFilePseudoItems()
        {
            using var temp = new TempDirectory();
            string alpha = temp.Write("alpha.h", "class Alpha { public: void Run(); };\n");
            string beta = temp.Write("beta.cpp", "int Beta(int value) { return value; }\n");
            using var index = new CppIndex();

            await index.BuildAsync(new[] { Source(alpha), Source(beta) });

            Assert.Equal(IndexState.Ready, index.State);
            Assert.Equal(2, index.FileCount);
            Assert.Equal(5, index.ItemCount);

            IReadOnlyList<NavigationItem> snapshot = index.Snapshot();
            Assert.Equal(5, snapshot.Count);

            Assert.Equal(ItemKind.File, snapshot[0].Kind);
            Assert.Equal("alpha.h", snapshot[0].Name);
            Assert.Equal(alpha, snapshot[0].FullName);
            Assert.Equal(ItemKind.Class, snapshot[1].Kind);
            Assert.Equal("Alpha", snapshot[1].Name);
            Assert.Equal(ItemKind.Method, snapshot[2].Kind);
            Assert.Equal("Run", snapshot[2].Name);
            Assert.Equal(ItemKind.File, snapshot[3].Kind);
            Assert.Equal("beta.cpp", snapshot[3].Name);
            Assert.Equal(ItemKind.Function, snapshot[4].Kind);
            Assert.Equal("Beta", snapshot[4].Name);

            IndexProgress progress = index.Progress;
            Assert.Equal(2, progress.FilesTotal);
            Assert.Equal(2, progress.FilesProcessed);
            Assert.True(progress.IsComplete);
            Assert.Equal(5, progress.ItemCount);
        }

        [Fact]
        public async Task BuildAsyncRaisesProgressAndItemsChangedEvents()
        {
            using var temp = new TempDirectory();
            string alpha = temp.Write("alpha.h", "class Alpha { public: void Run(); };\n");
            string beta = temp.Write("beta.cpp", "int Beta(int value) { return value; }\n");
            using var index = new CppIndex();

            var sync = new object();
            var progressEvents = new List<IndexProgress>();
            var itemEvents = new List<IndexItemsChangedEventArgs>();
            index.ProgressChanged += (sender, args) =>
            {
                lock (sync)
                {
                    progressEvents.Add(args.Progress);
                }
            };
            index.ItemsChanged += (sender, args) =>
            {
                lock (sync)
                {
                    itemEvents.Add(args);
                }
            };

            await index.BuildAsync(new[] { Source(alpha), Source(beta) });

            List<IndexProgress> progresses;
            List<IndexItemsChangedEventArgs> changed;
            lock (sync)
            {
                progresses = new List<IndexProgress>(progressEvents);
                changed = new List<IndexItemsChangedEventArgs>(itemEvents);
            }

            Assert.True(progresses.Count >= 2);
            Assert.True(progresses[progresses.Count - 1].IsComplete);
            Assert.Single(changed);
            Assert.True(changed[0].IsFullRebuild);
            Assert.Null(changed[0].FilePath);
            Assert.Equal(5, changed[0].Items.Count);
        }

        [Fact]
        public async Task UpdateFileAsyncReplacesOnlyThatFilesItems()
        {
            using var temp = new TempDirectory();
            string alpha = temp.Write("alpha.h", "class Alpha { public: void Run(); };\n");
            string beta = temp.Write("beta.cpp", "int Beta(int value) { return value; }\n");
            using var index = new CppIndex();
            await index.BuildAsync(new[] { Source(alpha), Source(beta) });

            var sync = new object();
            var itemEvents = new List<IndexItemsChangedEventArgs>();
            index.ItemsChanged += (sender, args) =>
            {
                lock (sync)
                {
                    itemEvents.Add(args);
                }
            };

            temp.Write("alpha.h", "class Gamma { public: int Size; };\n");
            await index.UpdateFileAsync(Source(alpha));

            IReadOnlyList<NavigationItem> snapshot = index.Snapshot();
            Assert.Null(IndexingTestData.Find(snapshot, "Alpha"));
            Assert.Null(IndexingTestData.Find(snapshot, "Run"));
            Assert.NotNull(IndexingTestData.Find(snapshot, "Gamma"));
            Assert.NotNull(IndexingTestData.Find(snapshot, "Size"));
            Assert.NotNull(IndexingTestData.Find(snapshot, "Beta"));
            Assert.Equal(2, index.FileCount);
            Assert.Equal(5, index.ItemCount);

            List<IndexItemsChangedEventArgs> changed;
            lock (sync)
            {
                changed = new List<IndexItemsChangedEventArgs>(itemEvents);
            }

            Assert.Single(changed);
            Assert.Equal(alpha, changed[0].FilePath);
            Assert.False(changed[0].IsFullRebuild);
            Assert.Equal(3, changed[0].Items.Count);
        }

        [Fact]
        public async Task UpdateFileAsyncIsNoOpForUntrackedFile()
        {
            using var temp = new TempDirectory();
            string alpha = temp.Write("alpha.h", "class Alpha {};\n");
            string unknown = temp.Write("unknown.cpp", "class Unknown {};\n");
            using var index = new CppIndex();
            await index.BuildAsync(new[] { Source(alpha) });

            int fileCount = index.FileCount;
            int itemCount = index.ItemCount;
            await index.UpdateFileAsync(Source(unknown));

            Assert.Equal(fileCount, index.FileCount);
            Assert.Equal(itemCount, index.ItemCount);
            Assert.Null(IndexingTestData.Find(index.Snapshot(), "Unknown"));
        }

        [Fact]
        public async Task RemoveFileRemovesFileAndItsItems()
        {
            using var temp = new TempDirectory();
            string alpha = temp.Write("alpha.h", "class Alpha {};\n");
            string beta = temp.Write("beta.cpp", "class Beta {};\n");
            using var index = new CppIndex();
            await index.BuildAsync(new[] { Source(alpha), Source(beta) });

            var sync = new object();
            var itemEvents = new List<IndexItemsChangedEventArgs>();
            index.ItemsChanged += (sender, args) =>
            {
                lock (sync)
                {
                    itemEvents.Add(args);
                }
            };

            index.RemoveFile(alpha);

            Assert.Equal(1, index.FileCount);
            Assert.Equal(2, index.ItemCount);
            Assert.Null(IndexingTestData.Find(index.Snapshot(), "Alpha"));
            Assert.NotNull(IndexingTestData.Find(index.Snapshot(), "Beta"));

            List<IndexItemsChangedEventArgs> changed;
            lock (sync)
            {
                changed = new List<IndexItemsChangedEventArgs>(itemEvents);
            }

            Assert.Single(changed);
            Assert.Equal(alpha, changed[0].FilePath);
            Assert.Empty(changed[0].Items);
        }

        [Fact]
        public async Task ClearRemovesAllContentAndResetsState()
        {
            using var temp = new TempDirectory();
            string alpha = temp.Write("alpha.h", "class Alpha {};\n");
            using var index = new CppIndex();
            await index.BuildAsync(new[] { Source(alpha) });

            index.Clear();

            Assert.Equal(0, index.FileCount);
            Assert.Equal(0, index.ItemCount);
            Assert.Equal(IndexState.NotStarted, index.State);
            Assert.Empty(index.Snapshot());
            Assert.Equal(IndexProgress.Empty.ToString(), index.Progress.ToString());
        }

        [Fact]
        public async Task CancelledBuildKeepsPreviousContentAndDoesNotThrow()
        {
            using var temp = new TempDirectory();
            string alpha = temp.Write("alpha.h", "class Alpha {};\n");
            using var index = new CppIndex();
            await index.BuildAsync(new[] { Source(alpha) });

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await index.BuildAsync(new[] { Source(alpha) }, cancellation.Token);

            Assert.Equal(IndexState.Ready, index.State);
            Assert.Equal(1, index.FileCount);
            Assert.NotNull(IndexingTestData.Find(index.Snapshot(), "Alpha"));
        }

        [Fact]
        public async Task DefaultOptionsIndexCudaHeadersAndTemplateImplementationFiles()
        {
            using var temp = new TempDirectory();
            string cudaHeader = temp.Write("kernels.cuh", "class CudaKernel {};\n");
            string templateImplementation = temp.Write("container.tcc", "int TemplateHelper();\n");
            using var index = new CppIndex();

            await index.BuildAsync(new[] { Source(cudaHeader), Source(templateImplementation) });

            Assert.Equal(2, index.FileCount);
            Assert.NotNull(IndexingTestData.Find(index.Snapshot(), "CudaKernel", ItemKind.Class));
            Assert.NotNull(IndexingTestData.Find(index.Snapshot(), "TemplateHelper", ItemKind.Function));
        }

        [Fact]
        public async Task BuildSkipsMissingUnsupportedOversizedAndExcludedFiles()
        {
            using var temp = new TempDirectory();
            string valid = temp.Write("valid.h", "class Ok {};\n");
            string missing = temp.Combine("does_not_exist.h");
            string large = temp.Write("large.cpp", "int Ignored();\n" + new string(' ', 3000));
            string text = temp.Write("notes.txt", "int NotCpp();\n");
            string excluded = temp.Write("build/generated.cpp", "int Generated();\n");
            var options = new CtrlTNavOptions { MaxFileSizeKb = 1 };
            using var index = new CppIndex(options);

            await index.BuildAsync(new[]
            {
                Source(valid),
                Source(missing),
                Source(large),
                Source(text),
                Source(excluded),
            });

            Assert.Equal(1, index.FileCount);
            Assert.Equal(2, index.ItemCount);
            Assert.NotNull(IndexingTestData.Find(index.Snapshot(), "Ok"));
            Assert.Null(IndexingTestData.Find(index.Snapshot(), "Generated"));
            Assert.Null(IndexingTestData.Find(index.Snapshot(), "Ignored"));
            Assert.Null(IndexingTestData.Find(index.Snapshot(), "NotCpp"));
        }

        [Fact]
        public async Task BuildToleratesUnreadableFile()
        {
            using var temp = new TempDirectory();
            string locked = temp.Write("locked.h", "class Locked {};\n");
            using var index = new CppIndex();

            using (var stream = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await index.BuildAsync(new[] { Source(locked) });
                Assert.Equal(0, index.FileCount);
            }

            await index.BuildAsync(new[] { Source(locked) });
            Assert.Equal(1, index.FileCount);
            Assert.NotNull(IndexingTestData.Find(index.Snapshot(), "Locked"));
        }

        [Fact]
        public async Task BuildDeduplicatesFilesAndToleratesEmptyInput()
        {
            using var temp = new TempDirectory();
            string alpha = temp.Write("alpha.h", "class Alpha {};\n");
            using var index = new CppIndex();

            await index.BuildAsync(new[] { Source(alpha), Source(alpha), Source(alpha) });
            Assert.Equal(1, index.FileCount);

            await index.BuildAsync(Array.Empty<SourceFileInfo>());
            Assert.Equal(0, index.FileCount);
            Assert.Empty(index.Snapshot());
            Assert.Equal(IndexState.Ready, index.State);
        }

        [Fact]
        public async Task ConcurrentBuildsAreSerializedAndComplete()
        {
            using var temp = new TempDirectory();
            string alpha = temp.Write("alpha.h", "class Alpha {};\n");
            string beta = temp.Write("beta.cpp", "class Beta {};\n");
            using var index = new CppIndex();

            Task first = Task.Run(() => index.BuildAsync(new[] { Source(alpha) }));

            // Task.Run does not guarantee execution order; wait until the first build has been accepted
            // so the test deterministically checks serialization instead of thread scheduling.
            Assert.True(
                SpinWait.SpinUntil(() => index.State != IndexState.NotStarted, TimeSpan.FromSeconds(5)),
                "The first build did not start.");
            Task second = Task.Run(() => index.BuildAsync(new[] { Source(alpha), Source(beta) }));
            await Task.WhenAll(first, second);

            Assert.Equal(2, index.FileCount);
            Assert.NotNull(IndexingTestData.Find(index.Snapshot(), "Beta"));
        }

        [Fact]
        public async Task SnapshotIsSafeForConcurrentReadersWhileUpdating()
        {
            using var temp = new TempDirectory();
            var files = new List<SourceFileInfo>();
            for (int fileIndex = 0; fileIndex < 20; fileIndex++)
            {
                string path = temp.Write("file" + fileIndex + ".h", "class Type" + fileIndex + " {};\n");
                files.Add(Source(path));
            }

            using var index = new CppIndex();
            await index.BuildAsync(files);

            var failures = new List<Exception>();
            var sync = new object();
            var readers = new List<Task>();
            for (int reader = 0; reader < 4; reader++)
            {
                readers.Add(Task.Run(() =>
                {
                    try
                    {
                        for (int iteration = 0; iteration < 100; iteration++)
                        {
                            IReadOnlyList<NavigationItem> snapshot = index.Snapshot();
                            int itemTotal = 0;
                            for (int item = 0; item < snapshot.Count; item++)
                            {
                                if (snapshot[item] != null)
                                {
                                    itemTotal++;
                                }
                            }

                            Assert.Equal(snapshot.Count, itemTotal);
                        }
                    }
                    catch (Exception exception)
                    {
                        lock (sync)
                        {
                            failures.Add(exception);
                        }
                    }
                }));
            }

            Task updater = Task.Run(async () =>
            {
                try
                {
                    for (int iteration = 0; iteration < 20; iteration++)
                    {
                        await index.UpdateFileAsync(files[iteration % files.Count]);
                    }
                }
                catch (Exception exception)
                {
                    lock (sync)
                    {
                        failures.Add(exception);
                    }
                }
            });

            await Task.WhenAll(readers.Concat(new[] { updater }));
            Assert.Empty(failures);
            Assert.Equal(20, index.FileCount);
        }

        [Fact]
        public async Task DisposeReleasesContentAndMakesSubsequentCallsNoOps()
        {
            using var temp = new TempDirectory();
            string alpha = temp.Write("alpha.h", "class Alpha {};\n");
            var index = new CppIndex();
            await index.BuildAsync(new[] { Source(alpha) });
            index.Dispose();

            Assert.Equal(0, index.FileCount);
            Assert.Equal(0, index.ItemCount);
            Assert.Empty(index.Snapshot());

            await index.BuildAsync(new[] { Source(alpha) });
            await index.UpdateFileAsync(Source(alpha));
            index.RemoveFile(alpha);
            index.Clear();
            Assert.Empty(index.Snapshot());
        }
    }
}
