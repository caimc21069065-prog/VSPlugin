# CtrlTNav.Matching

Fuzzy matching, ranking, the in-memory search index and the recent-usage (MRU) store for the
Ctrl+T Navigator extension. Implements the contract in `docs/ARCHITECTURE.md` section 4.

Targets: `net472` (VSIX) and `net8.0` (tests, smoke tool). No NuGet dependencies beyond the
.NET Framework reference assemblies pulled in by `Directory.Build.props`.

## Public surface

| Type | Purpose |
|------|---------|
| `SearchIndex : ISearchIndex` | Thread-safe (single writer / many readers) in-memory index with the full ranking pipeline. |
| `RecentUsageTracker : IRecentUsageStore, IRecentUsagePersistence` | Persistent MRU list producing a 0..1000 strength per item. |
| `FuzzyMatcher`, `FuzzyMatchResult` | Standalone fuzzy matcher used by the index (also handy for tooling/tests). |

## Match classes

Best first (weights from `SearchOptions`, defaults in parentheses):

| Class | Meaning | Default bonus |
|-------|---------|---------------|
| `Exact` | Whole name equals the query (case-insensitive). | 1200 |
| `Prefix` | Name starts with the query. | 600 |
| `Camel` | Query characters are word starts (`getUserName`, `get_user_name`, `get::User::Name` ← `gun`) or a contiguous run starting at a word start (`getName` ← `getUserName`). | 300 |
| `Subsequence` | Query characters appear in order (gaps allowed). | 0 |
| `FullName` | Match was only found on `NavigationItem.FullName` (or the file path of a file entry). No highlight ranges. | 120 |

Within a class a **quality score of 0..299** breaks ties: shorter prefixes, more word-boundary
hits, more contiguous runs, an earlier match start and a smaller match span score higher. The
quality range is deliberately smaller than the 300-point class gap so a worse class can never
outrank a better one through quality alone.

## Final score

```
score = classBonus + classQuality
      + MRU boost      (store strength 0..1000 scaled to 0..RecentUsageBoostMax, default 500)
      + currentProject (default 150, when item.ProjectPath == options.CurrentProjectPath)
      + publicSymbol   (default 40, when ItemFlags.Public or ItemFlags.Exported is set)
```

Results are sorted by score descending, then by shorter `Item.Name`, then ordinal name, then
item identity (fully deterministic), truncated to `MaxResults` with a bounded top-K heap.
MRU can lift a common symbol above a slightly better fuzzy match (500 > 300 point class gap) but
cannot beat an exact match (the 1200/600 exact-vs-prefix gap is never exceeded by quality + boosts
under the default weights).

Highlight ranges are computed **only for the returned hits** and are always relative to
`Item.Name`. `MatchKind.FullName` hits have empty ranges.

## Empty query ("browse")

An empty query (also `"t:"`, `"f:"`, `"# "` with no text) returns the popup's initial content:

1. recently used items, newest first (resolved through the `RecentUsageTracker` snapshot);
2. public/exported symbols, ordered by publicness, kind, name;
3. files, ordered by name.

With an explicit filter prefix the "public only" rule is relaxed so e.g. `t:` lists every type.
The ordering is deterministic, so re-running the same query yields the same list.

## Thread safety

* `SearchIndex`: mutations (`AddOrUpdate`, `AddRange`, `ReplaceFile`, `RemoveFile`, `Clear`) must
  come from a single writer thread (the indexer). Any number of threads may call `Search`
  concurrently: readers scan an immutable item-array snapshot that is rebuilt lazily after a
  mutation, so a search never observes a partially applied update and never holds a lock while
  scanning.
* `RecentUsageTracker`: all public members are thread-safe. Searches read an immutable MRU
  snapshot, so `GetBoost`/`GetStrength` are lock-free and do not allocate identity strings on the
  index hot path (`SearchIndex` uses the internal identity lookup).

## Performance notes

The hot path pre-computes per item on insert: lower-cased name, 36-bit ASCII presence mask,
64-bit token-boundary mask and the identity string. A search therefore performs no
`ToLower()`, `Substring` or `GetIdentity()` per candidate and does not allocate while scanning.
The ASCII presence mask rejects most candidates with a single bitwise AND; the boundary mask makes
the camel-case rule a few bit tests. Per-search allocations are limited to the result objects
(at most `MaxResults`), their highlight ranges, one lower-cased query string and the top-K heap.

Measured on the development machine (see `SearchIndexPerformanceTests`, 100 000 items,
`MaxResults = 120`): numbers are printed by the test run and kept in the team report.

## Persistence format (`RecentUsageTracker`)

`Load`/`Save` use a small line-oriented UTF-8 file (default `%LOCALAPPDATA%\CtrlTNav\recent-usage.dat`):

```
CtrlTNav.RecentUsage.v1
<sequence>\t<count>\t<lastUsedUtcTicks>\t<kind>\t<line>\t<column>\t<identity>\t<name>\t<filePath>
```

Text fields are backslash-escaped (`\\`, `\t`, `\n`, `\r`). `Save` writes a temp file and
replaces the target (tolerating file systems where `File.Replace` is unavailable); missing or
corrupt files never throw — corrupt lines are skipped and an unreadable file yields an empty
store. `Load` must be called explicitly after construction (the VS integration owns that
lifecycle and debounces `Save`).

## Notes for consumers

* Build `SearchOptions` once per keystroke from the options snapshot
  (`CtrlTNavOptions.ToSearchOptions(currentProjectPath)`); `Search` calls `Normalized()` for you.
* `ISearchIndex.Search` never throws; failures are logged through the injected `ILog` and produce
  an empty result list.
* `SearchIndex.RecentUsage` can point at any `IRecentUsageStore`; foreign implementations are
  queried item by item (allocation-friendly fallback), `RecentUsageTracker` uses a fast internal
  path.
* Set `SearchOptions.IncludeFiles`/`IncludeSymbols` to hide file/symbol results globally; the
  filter prefixes (`t:`, `f:`, `#`, `m:`) select the item kinds and are additionally intersected
  with those switches.
