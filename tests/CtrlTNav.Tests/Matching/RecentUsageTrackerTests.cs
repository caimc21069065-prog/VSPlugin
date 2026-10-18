using System;
using System.Collections.Generic;
using System.IO;
using CtrlTNav.Core.Model;
using CtrlTNav.Matching;
using Xunit;

namespace CtrlTNav.Tests.Matching
{
    /// <summary>
    /// Tests for <see cref="RecentUsageTracker"/>: recency/frequency boost, capacity eviction, recent file
    /// paths, persistence round-trips and tolerance against missing/corrupt files.
    /// </summary>
    public sealed class RecentUsageTrackerTests
    {
        private static NavigationItem Item(string name, string path, int line = 1, ItemKind kind = ItemKind.Method)
        {
            return new NavigationItem(name, kind, path, line) { FullName = name };
        }

        [Fact]
        public void NewTracker_IsEmpty_AndBoostsEverythingByZero()
        {
            using (var directory = new TempDirectory())
            {
                var tracker = new RecentUsageTracker(directory.File("recent.dat"));
                Assert.True(tracker.IsEmpty);

                NavigationItem item = Item("Alpha", @"C:\p\a.h");
                Assert.Equal(0, tracker.GetBoost(item));
                Assert.Empty(tracker.GetRecentFilePaths(10));
            }
        }

        [Fact]
        public void RecentItem_GetsHigherBoostThanOlderItem()
        {
            using (var directory = new TempDirectory())
            {
                var tracker = new RecentUsageTracker(directory.File("recent.dat"));
                NavigationItem older = Item("Alpha", @"C:\p\a.h");
                NavigationItem newer = Item("Beta", @"C:\p\b.h");

                tracker.RecordUsage(older);
                tracker.RecordUsage(newer);

                int olderBoost = tracker.GetBoost(older);
                int newerBoost = tracker.GetBoost(newer);

                Assert.InRange(olderBoost, 0, 1000);
                Assert.InRange(newerBoost, 0, 1000);
                Assert.True(newerBoost > olderBoost, "newer=" + newerBoost + " older=" + olderBoost);
                Assert.False(tracker.IsEmpty);
            }
        }

        [Fact]
        public void RepeatedUsage_OfTheSameItem_IncreasesItsBoost()
        {
            using (var directory = new TempDirectory())
            {
                var tracker = new RecentUsageTracker(directory.File("recent.dat"));
                NavigationItem frequent = Item("Frequent", @"C:\p\f.h");
                NavigationItem occasional = Item("Occasional", @"C:\p\o.h");

                int before = tracker.GetBoost(frequent);
                for (int i = 0; i < 10; i++)
                {
                    tracker.RecordUsage(frequent);
                }

                int after = tracker.GetBoost(frequent);
                tracker.RecordUsage(occasional);

                Assert.True(after > before);
                Assert.Equal(1000, after); // 700 recency + 300 frequency (capped)
                Assert.True(after > tracker.GetBoost(occasional));
            }
        }

        [Fact]
        public void Boost_IsAlwaysInRange()
        {
            using (var directory = new TempDirectory())
            {
                var tracker = new RecentUsageTracker(directory.File("recent.dat"), capacity: 5);
                for (int i = 0; i < 50; i++)
                {
                    NavigationItem item = Item("Item" + i, @"C:\p\a.h", i + 1);
                    tracker.RecordUsage(item);
                    Assert.InRange(tracker.GetBoost(item), 0, 1000);
                }
            }
        }

        [Fact]
        public void RecordUsage_SameIdentity_Twice_DoesNotDuplicate()
        {
            using (var directory = new TempDirectory())
            {
                var tracker = new RecentUsageTracker(directory.File("recent.dat"));
                NavigationItem first = Item("Alpha", @"C:\p\a.h", 3);
                NavigationItem equivalent = Item("Alpha", @"C:\p\a.h", 3); // same identity, different instance

                tracker.RecordUsage(first);
                tracker.RecordUsage(equivalent);

                Assert.Equal(1, tracker.EntryCount);
                Assert.True(tracker.GetBoost(first) > 0);
                Assert.Equal(tracker.GetBoost(first), tracker.GetBoost(equivalent));
            }
        }

        [Fact]
        public void RecordUsage_Null_IsIgnored()
        {
            using (var directory = new TempDirectory())
            {
                var tracker = new RecentUsageTracker(directory.File("recent.dat"));
                tracker.RecordUsage(null!);
                Assert.True(tracker.IsEmpty);
                Assert.Equal(0, tracker.GetBoost(null!));
            }
        }

        [Fact]
        public void GetRecentFilePaths_ReturnsNewestFirstAndDistinct()
        {
            using (var directory = new TempDirectory())
            {
                var tracker = new RecentUsageTracker(directory.File("recent.dat"));
                NavigationItem fileA = Item("Alpha", @"C:\p\a.h");
                NavigationItem fileB = Item("Beta", @"C:\p\b.h");
                NavigationItem fileC = Item("Gamma", @"C:\p\c.h");

                tracker.RecordUsage(fileA);
                tracker.RecordUsage(fileB);
                tracker.RecordUsage(fileA); // A is now the newest, and must not appear twice
                tracker.RecordUsage(fileC);

                IReadOnlyList<string> paths = tracker.GetRecentFilePaths(10);

                Assert.Equal(new[] { @"C:\p\c.h", @"C:\p\a.h", @"C:\p\b.h" }, paths);
                Assert.Equal(2, tracker.GetRecentFilePaths(2).Count);
                Assert.Empty(tracker.GetRecentFilePaths(0));
            }
        }

        [Fact]
        public void Capacity_EvictsOldestEntries()
        {
            using (var directory = new TempDirectory())
            {
                var tracker = new RecentUsageTracker(directory.File("recent.dat"), capacity: 2);
                NavigationItem first = Item("First", @"C:\p\1.h");
                NavigationItem second = Item("Second", @"C:\p\2.h");
                NavigationItem third = Item("Third", @"C:\p\3.h");

                tracker.RecordUsage(first);
                tracker.RecordUsage(second);
                tracker.RecordUsage(third);

                Assert.Equal(2, tracker.EntryCount);
                Assert.Equal(0, tracker.GetBoost(first));
                Assert.True(tracker.GetBoost(second) > 0);
                Assert.True(tracker.GetBoost(third) > 0);
            }
        }

        [Fact]
        public void Clear_EmptiesTheTracker()
        {
            using (var directory = new TempDirectory())
            {
                var tracker = new RecentUsageTracker(directory.File("recent.dat"));
                tracker.RecordUsage(Item("Alpha", @"C:\p\a.h"));
                Assert.False(tracker.IsEmpty);

                tracker.Clear();

                Assert.True(tracker.IsEmpty);
                Assert.Empty(tracker.GetRecentFilePaths(10));
            }
        }

        [Fact]
        public void SaveAndLoad_RoundTripsBoostsAndRecentPaths()
        {
            using (var directory = new TempDirectory())
            {
                string path = directory.File("recent.dat");
                NavigationItem alpha = Item("Alpha", @"C:\p\a.h", 1, ItemKind.Class);
                NavigationItem beta = Item("Beta", @"C:\p\b.h", 2, ItemKind.Method);
                NavigationItem gamma = Item("Gamma", @"C:\p\c.h", 3, ItemKind.File);

                var original = new RecentUsageTracker(path);
                original.RecordUsage(alpha);
                original.RecordUsage(beta);
                original.RecordUsage(gamma);
                original.RecordUsage(gamma);
                original.Save();

                Assert.True(File.Exists(path));

                var reloaded = new RecentUsageTracker(path);
                reloaded.Load();

                Assert.Equal(3, reloaded.EntryCount);
                Assert.Equal(original.GetBoost(alpha), reloaded.GetBoost(alpha));
                Assert.Equal(original.GetBoost(beta), reloaded.GetBoost(beta));
                Assert.Equal(original.GetBoost(gamma), reloaded.GetBoost(gamma));
                Assert.Equal(
                    new[] { @"C:\p\c.h", @"C:\p\b.h", @"C:\p\a.h" },
                    reloaded.GetRecentFilePaths(10));
                Assert.False(File.Exists(path + ".tmp"));
            }
        }

        [Fact]
        public void SaveTwice_ReplacesTheFile()
        {
            using (var directory = new TempDirectory())
            {
                string path = directory.File("recent.dat");
                var tracker = new RecentUsageTracker(path);
                tracker.RecordUsage(Item("Alpha", @"C:\p\a.h"));
                tracker.Save();
                tracker.RecordUsage(Item("Beta", @"C:\p\b.h"));
                tracker.Save();

                var reloaded = new RecentUsageTracker(path);
                reloaded.Load();
                Assert.Equal(2, reloaded.EntryCount);
            }
        }

        [Fact]
        public void Load_MissingFile_YieldsEmptyTrackerWithoutThrowing()
        {
            using (var directory = new TempDirectory())
            {
                var tracker = new RecentUsageTracker(directory.File("does-not-exist.dat"));
                tracker.Load();
                Assert.True(tracker.IsEmpty);
            }
        }

        [Fact]
        public void Load_CorruptFile_YieldsEmptyTrackerWithoutThrowing()
        {
            using (var directory = new TempDirectory())
            {
                string path = directory.File("corrupt.dat");
                File.WriteAllText(path, "\u0000\u0001 not a recent usage file \t\t garbage");

                var log = new RecordingLog();
                var tracker = new RecentUsageTracker(path, log: log);
                tracker.Load();

                Assert.True(tracker.IsEmpty);
                Assert.NotEmpty(log.Warnings);
            }
        }

        [Fact]
        public void Load_CorruptLines_AreSkippedButValidEntriesSurvive()
        {
            using (var directory = new TempDirectory())
            {
                string path = directory.File("recent.dat");
                NavigationItem alpha = Item("Alpha", @"C:\p\a.h");
                NavigationItem beta = Item("Beta", @"C:\p\b.h");

                var original = new RecentUsageTracker(path);
                original.RecordUsage(alpha);
                original.RecordUsage(beta);
                original.Save();

                File.AppendAllText(path, "\r\nnot\ta\tvalid\tline\r\n\r\n");

                var reloaded = new RecentUsageTracker(path);
                reloaded.Load();

                Assert.Equal(2, reloaded.EntryCount);
                Assert.True(reloaded.GetBoost(alpha) > 0);
                Assert.True(reloaded.GetBoost(beta) > 0);
            }
        }

        [Fact]
        public void Save_ToUnwritablePath_DoesNotThrow()
        {
            var log = new RecordingLog();
            var tracker = new RecentUsageTracker(@"Z:\definitely\missing\drive\recent.dat", log: log);

            tracker.RecordUsage(Item("Alpha", @"C:\p\a.h"));
            tracker.Save(); // must swallow the IO error

            Assert.NotEmpty(log.Warnings);
        }

        [Fact]
        public void SaveAndLoad_SurvivesWeirdNamesAndPaths()
        {
            using (var directory = new TempDirectory())
            {
                string path = directory.File("recent.dat");
                NavigationItem weird = new NavigationItem("Weird\tName\nX", ItemKind.Macro, @"C:\p\dir with space\weird.h", 5)
                {
                    FullName = "ns::Weird\tName\nX",
                };

                var original = new RecentUsageTracker(path);
                original.RecordUsage(weird);
                original.Save();

                var reloaded = new RecentUsageTracker(path);
                reloaded.Load();

                Assert.Equal(1, reloaded.EntryCount);
                Assert.Equal(original.GetBoost(weird), reloaded.GetBoost(weird));
                Assert.Equal(weird.FilePath, reloaded.GetRecentFilePaths(1)[0]);
            }
        }

        [Fact]
        public void RecordUsage_AfterLoad_NewEntryBecomesMostRecent()
        {
            using (var directory = new TempDirectory())
            {
                string path = directory.File("recent.dat");
                NavigationItem alpha = Item("Alpha", @"C:\p\a.h");
                NavigationItem beta = Item("Beta", @"C:\p\b.h");

                var original = new RecentUsageTracker(path);
                original.RecordUsage(alpha);
                original.Save();

                var reloaded = new RecentUsageTracker(path);
                reloaded.Load();
                reloaded.RecordUsage(beta);

                Assert.Equal(new[] { @"C:\p\b.h", @"C:\p\a.h" }, reloaded.GetRecentFilePaths(10));
            }
        }

        [Fact]
        public void Constructor_RejectsNothingAndClampsCapacity()
        {
            using (var directory = new TempDirectory())
            {
                var tracker = new RecentUsageTracker(directory.File("recent.dat"), capacity: 0);
                Assert.Equal(1, tracker.Capacity);

                for (int i = 0; i < 5; i++)
                {
                    tracker.RecordUsage(Item("Item" + i, @"C:\p\a.h", i + 1));
                }

                Assert.Equal(1, tracker.EntryCount);
            }
        }

        [Fact]
        public void StorageFilePath_IsExposed()
        {
            using (var directory = new TempDirectory())
            {
                string path = directory.File("recent.dat");
                var tracker = new RecentUsageTracker(path);
                Assert.Equal(path, tracker.StorageFilePath);
            }
        }
    }
}
