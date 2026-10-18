# Ctrl+T Navigator for C++ — Architecture and Implementation Contract

Status: **frozen v1.0** (Lead-owned). Source spec: `VS2026-CtrlT-ReSharper-C++-Navigation-Plugin.md`.

This document is the implementation contract for every assembly. The public types described in
"Frozen contracts" are already checked in under `src/CtrlTNav.Core` — do not change their public
shape without asking the Team Lead, because other assemblies are authored against them in parallel.

---

## 1. Product scope

A Visual Studio 2026 (and VS 2022 17.x compatible) VSSDK extension that implements a ReSharper-like
**Ctrl+T / Go to Everything** experience for C/C++:

| Spec | Requirement | Where implemented |
|------|-------------|-------------------|
| F1 | Ctrl+T opens the search popup | VSCT key binding + `SearchWindowController` |
| F2 | Unified search over types, symbols, files | `CtrlTNav.Indexing` + `CtrlTNav.Matching` |
| F3 | Live filtering while typing | `SearchWindow` + `SearchIndex.Search` |
| F4 | Fuzzy matching (prefix, camel, subsequence) | `CtrlTNav.Matching.FuzzyMatcher` |
| F5 | Smart ranking (exact > prefix > camel > fuzzy, MRU) | `SearchIndex` ranking pipeline |
| F6 | Enter navigates to file + position | `VsNavigator` (DTE) |
| F7 | Filter prefixes `t `, `f `, `# ` | `SearchQuery.Parse` (Core) |
| F8 | Kind badges, path, line number in list | `SearchWindow` / `ResultRow` |
| F9 | Recently used weighting | `RecentUsageTracker` |
| F10 | Option to override native Ctrl+T | options page + `GoToEverythingCommand` fallback |
| F11 | Ctrl+, backup shortcut | second VSCT key binding / command |
| NF1 | Popup appears in ≤ 50 ms | window pre-created & hidden; index in memory |
| NF2 | Filtering feels instant | allocation-light matching, top-K heap, 15 ms debounce |
| NF3 | First index in background | `CppIndex.BuildAsync` (parallel, cancellable) |
| NF4 | Incremental re-index ≤ 2 s | save/close/file-watch → `UpdateFileAsync` |
| NF5 | Reasonable memory | compact `NavigationItem` graph, no per-keystroke copies |
| NF6 | Incomplete code tolerated | tolerant hand-written C++ tokenizer |

Out of scope (per spec): code analysis, refactoring, code generation, 100% semantic correctness for
complex macros/templates, clangd integration in v1.

---

## 2. Solution layout

```
CtrlTNav.sln
Directory.Build.props            shared compiler settings (net472 + net8.0 friendly)
global.json / NuGet.config
docs/                            architecture, build, acceptance evidence
src/
  CtrlTNav.Core/                 contracts + models + settings (net472;net8.0)   [Lead]
  CtrlTNav.Matching/             fuzzy matching, ranking, search index, MRU   [core-engine]
  CtrlTNav.Indexing/             C++ extraction, solution scanning, cache     [indexer]
  CtrlTNav.UI/                   WPF popup window (net472;net8.0-windows)     [ui]
  CtrlTNav.Vsix/                 VSSDK package, commands, options, adapters   [vs-integration]
tests/
  CtrlTNav.Tests/                xUnit tests (net8.0)                         [core-engine, indexer]
tools/
  CtrlTNav.Smoke/                console end-to-end harness over real dirs     [Lead]
  CtrlTNav.UiDemo/               renders SearchWindow mockups to PNG           [ui]
```

Dependency graph (project references, no cycles):

```
Core  <-  Matching
Core  <-  Indexing
Core  <-  UI
Core  <-  Vsix, Matching <- Vsix, Indexing <- Vsix, UI <- Vsix
Core/Matching/Indexing  <-  Tests (tests never reference Vsix/UI)
UI  <-  UiDemo (net8.0-windows only)
Matching/Indexing/Core  <-  Smoke
```

`CtrlTNav.Core` is multi-targeted `net472;net8.0`; the tests, smoke tool and UI demo only build the
`net8.0` flavour, while the VSIX consumes `net472`.

---

## 3. Frozen contracts (already in `CtrlTNav.Core`)

Namespaces: `CtrlTNav.Core.Model`, `.Search`, `.Indexing`, `.Services`, `.Options`, `.Diagnostics`.

* `Model/NavigationItem.cs` — one navigable entry (file or symbol). Members:
  `Name`, `FullName`, `Kind`, `FilePath`, `Line` (1-based), `Column`, `ProjectName`, `ProjectPath`,
  `Flags`, `ContainerName`, `Signature`, `Language`, `IsFile`, `IsPublicSymbol`, `FileName`,
  `GetIdentity()`, `CloneWithFile(string)`.
* `Model/ItemKind.cs` — `ItemKind` enum, `ItemKindFilter` enum, `ItemKindExtensions`
  (`IsTypeKind`, `IsSymbolKind`, `IsMemberKind`, `IsCallableKind`, `MatchesFilter`, `GetShortCode`,
  `GetDisplayName`). `GetShortCode` feeds the icon badge in the UI.
* `Model/ItemFlags.cs` — access / static / exported / template / declaration / scope flags.
* `Model/MatchKind.cs` — `None < Subsequence < Camel < Prefix < Exact`; `FullName` for qualified-name hits.
* `Model/TextRange.cs` — highlight range over `NavigationItem.Name`.
* `Search/SearchQuery.cs` — `Parse(string?)`. Prefixes: `t ` / `t:` / `type:` types,
  `f ` / `f:` / `file:` files, `#` / `s ` / `sym:` / `symbol:` symbols, `m ` / `member:` members.
  Exposes `RawText`, `Text`, `Filter`, `HasFilterPrefix`, `IsEmpty`.
* `Search/SearchOptions.cs` — max results, filters, boost weights (`ExactMatchBonus`,
  `PrefixMatchBonus`, `CamelMatchBonus`, `SubsequenceMatchBonus`, `FullNameMatchBonus`,
  `RecentUsageBoostMax`, `CurrentProjectBonus`, `PublicSymbolBonus`), `CurrentProjectPath`, `Clone()`,
  `Normalized()`.
* `Search/SearchResult.cs` — `Item`, `Score`, `MatchKind`, `MatchedRanges` (relative to `Item.Name`).
* `Search/SearchResponse.cs` — `RawText`, `Filter`, `Results`, `TotalIndexed`, `ElapsedMs`.
* `Search/ISearchIndex.cs` — `Count`, `AddOrUpdate`, `AddRange`, `ReplaceFile`, `RemoveFile`, `Clear`,
  `Search(SearchQuery, SearchOptions)`.
* `Search/IRecentUsageStore.cs` — `RecordUsage`, `GetBoost` (0..1000 relative strength),
  `GetRecentFilePaths`, `IsEmpty`, `Clear`; plus `IRecentUsagePersistence.Load/Save`.
* `Search/ISearchService.cs` — UI-facing facade (state, progress, `Search`, `RecordUsage`, `StatusText`,
  `IndexStateChanged`). Implemented by **`CtrlTNav.Vsix.Services.VsSearchService`** (VS integration).
* `Services/INavigationService.cs` — `Navigate(item, out string? errorMessage)`.
* `Indexing/ICppIndex.cs` + `IndexProgress.cs` — background index: `BuildAsync`, `UpdateFileAsync`,
  `RemoveFile`, `Clear`, `Snapshot`, `ProgressChanged`, `ItemsChanged`, `State`.
* `Indexing/ISolutionFileProvider.cs`, `Indexing/SourceFileInfo.cs` — solution file enumeration input.
* `Options/CtrlTNavOptions.cs` — persisted settings POCO + `ToSearchOptions`, `Normalize`, `SplitList`.
* `Diagnostics/ILog.cs` — `ILog`, `NullLog`, `FileLog`.
* `AppPaths.cs` — `%LOCALAPPDATA%\CtrlTNav` locations (`recent-usage.dat`, log, cache files).
* `ArgbColor.cs` — theme colour primitive shared by VS integration and the WPF UI (no WPF types in Vsix).

**Never modify** the Core contracts to make another assembly compile; if a contract is insufficient,
message the Lead. Implementations live in their own assemblies.

---

## 4. `CtrlTNav.Matching` — required public API

Namespace `CtrlTNav.Matching`.

```csharp
public sealed class SearchIndex : ISearchIndex
{
    public SearchIndex(ILog? log = null);
    public IRecentUsageStore? RecentUsage { get; set; }   // consulted when options.BoostRecentlyUsed
    // interface members +:
    public int Count { get; }
    public IReadOnlyList<SearchResult> Search(SearchQuery query, SearchOptions options);
}

public sealed class RecentUsageTracker : IRecentUsageStore, IRecentUsagePersistence
{
    public RecentUsageTracker(string? storageFilePath = null, int capacity = 300, ILog? log = null);
    public void Load();            // tolerant: corrupt/missing file => empty store
    public void Save();            // atomic-ish write, swallows IO errors
    public void RecordUsage(NavigationItem item);
    public int GetBoost(NavigationItem item);             // 0..1000
    public IReadOnlyList<string> GetRecentFilePaths(int maxCount);
    public bool IsEmpty { get; }
    public void Clear();
}
```

`SearchIndex.Search` rules (must be covered by tests):

1. Candidates filtered by `query.Filter` (or `options.DefaultFilter` when no prefix) and by
   `IncludeFiles`/`IncludeSymbols`.
2. Empty query returns the best entries for the empty state: recently used items first, then public
   symbols, then files, deterministic (sort by kind/name).
3. Match kinds ordered `Exact > Prefix > Camel > Subsequence`; a qualified-name-only hit scores
   `FullName` + small bonus and has empty `MatchedRanges`.
4. Final score = match score + MRU boost (scaled by `RecentUsageBoostMax`) + current-project bonus +
   public/exported bonus. MRU must be able to lift a common symbol above a slightly better fuzzy match
   (spec F5/F9) but must not beat an exact match.
5. Results sorted by score desc, then shorter name, then ordinal name; at most `MaxResults` items.
6. `MatchedRanges` are computed **only for returned hits** (perf), relative to `Item.Name`.
7. Thread-safe for concurrent readers; writer lock around mutations. `Search` must not throw.
8. Performance target: 100k items, 6-char query, warm cache — under 30 ms on this machine (test with
   generous bound to avoid flakiness; measure and report actual number in test output).

`FuzzyMatcher` may be `public static`/`internal` as the owner prefers, but unit tests in
`CtrlTNav.Tests` cover: prefix, camel acronym (`gun` -> `getUserName`), underscore segmentation,
subsequence, exact, no-match, unicode/ASCII, empty query, case-insensitivity.

## 5. `CtrlTNav.Indexing` — required public API

Namespace `CtrlTNav.Indexing`.

```csharp
public sealed class CppIndex : ICppIndex
{
    public CppIndex(CtrlTNavOptions? options = null, ILog? log = null);
}

public sealed class CppSymbolExtractor
{
    public CppSymbolExtractor();
    public IReadOnlyList<NavigationItem> Extract(string filePath, string content,
        string projectName = "", string projectPath = "", string language = "cpp");
}

public static class SolutionFileParser
{
    public static IReadOnlyList<SourceFileInfo> ParseSolution(string solutionPath);
    public static IReadOnlyList<SourceFileInfo> ParseProject(string projectPath, string? projectName = null);
}

public static class SourceFileScanner
{
    public static IReadOnlyList<SourceFileInfo> ScanDirectory(string rootDirectory, CtrlTNavOptions options);
    public static bool IsSupportedFile(string path, CtrlTNavOptions options);
}

public static class GlobMatcher
{
    public static bool IsMatch(string path, string pattern);
    public static bool IsExcluded(string path, string[] patterns);
}

public sealed class IndexCache   // optional persistence layer, tested; wiring optional in v1
{
    public static IndexCache? Load(string cacheFilePath);
    public void Save(string cacheFilePath, string solutionPath, IEnumerable<IndexCacheEntry> entries);
    public IReadOnlyList<IndexCacheEntry> Entries { get; }
}

public sealed class IndexCacheEntry
{
    public string FilePath { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public long Length { get; set; }
    public IReadOnlyList<NavigationItem> Items { get; set; }
}
```

Extraction requirements (tests must cover each bullet):

* `namespace a::b {`, `namespace alias = ...`, anonymous namespaces.
* `class/struct/union/interface?/enum` declarations and definitions, nested types, forward declarations,
  `enum class`, enum members.
* Functions: free functions, members, constructors, destructors, `operator+`, `operator()`,
  methods with trailing `const`/`noexcept`/`override`, `->` return types, `template<...>` functions,
  multi-line signatures, default arguments, function pointer declarations, `extern "C"` blocks.
* Variables: global variables, `static` globals, class fields (with access level), `#define` macros
  (object-like and function-like), `typedef`, `using X = Y`, concepts (`concept Name = ...;`).
* Access levels for class members from `public:`/`private:`/`protected:` sections.
* Correct 1-based line numbers (and columns) for the symbol name token.
* Tolerance: never throw on malformed input; skip garbage; handle comments/strings/raw strings
  (`R"(...)"`), nested braces, `#if 0` blocks, incomplete functions at EOF.
* Ignore: local variables, function bodies' inner identifiers, include directives.
* File pseudo-item: `CppIndex` adds one `ItemKind.File` entry per indexed file (Name = file name).

`CppIndex` requirements: parallel extraction (`BuildAsync`, bounded concurrency, cancellable),
`UpdateFileAsync` replaces one file's items, `RemoveFile`, `Snapshot`, progress events, tolerant file
IO (unreadable file => skip, log), options-driven max file size / extensions / excludes.

`SolutionFileParser` requirements: parse `.sln` project entries (skip solution folders by project-type
GUID), then `.vcxproj`/`.csproj`? (C++ only: `.vcxproj`; also `.props` ignored) for
`<ClCompile Include>`, `<ClInclude Include>`, `<None Include>` C/C++ extensions, resolve relative paths
against the project directory, normalize separators; tolerate missing files and BOM/XML quirks.

## 6. `CtrlTNav.UI` — required public API

Namespace `CtrlTNav.UI`. **Programmatic WPF only (no XAML files)** so the project builds with the
plain .NET SDK on net472 and net8.0-windows.

```csharp
public sealed class SearchWindowController
{
    public static SearchWindowController Create(
        ISearchService searchService,
        INavigationService navigationService,
        SearchWindowTheme theme,
        SearchWindowOptions options);                // must be called on a WPF dispatcher thread

    public bool IsVisible { get; }
    public void ShowSearch(string? presetText = null);
    public void HideSearch();
    public void UpdateOptions(SearchWindowOptions options);
    public void ApplyTheme(SearchWindowTheme theme);
    public void Dispose();
}

public sealed class SearchWindowOptions
{
    public string? RelativePathBaseDirectory { get; set; }  // solution dir, for relative paths
    public bool ShowLineNumbers { get; set; } = true;
    public bool ShowRelativePaths { get; set; } = true;
    public bool CloseOnFocusLost { get; set; } = true;
    public string Placeholder { get; set; } = "Go to everything (Ctrl+T)";
}

public sealed class SearchWindowTheme
{
    public ArgbColor Background { get; set; }
    public ArgbColor Foreground { get; set; }
    public ArgbColor SubtleForeground { get; set; }
    public ArgbColor Border { get; set; }
    public ArgbColor SelectionBackground { get; set; }
    public ArgbColor SelectionForeground { get; set; }
    public ArgbColor Accent { get; set; }
    public ArgbColor InputBackground { get; set; }
    public ArgbColor BadgeForeground { get; set; }
    public ArgbColor CountForeground { get; set; }
    public static SearchWindowTheme Dark();
    public static SearchWindowTheme Light();
    public SearchWindowTheme Clone();
}
```

Behaviour contract:

* Borderless popup centered horizontally, ~12% from the top, width ~720 DIP, results list max height
  ~420 DIP, shadow, rounded corners.
* Search text box focused on show; text selected; typing triggers search after a 15 ms debounce; stale
  results from older queries must never overwrite newer ones (version counter).
* Result rows: kind badge (colour per kind family), name with bold accent highlight for
  `MatchedRanges`, right-aligned relative path + `:line`.
* Keyboard: Up/Down/PageUp/PageDown/Home/End move selection, Enter navigates, Esc closes, Tab cycles
  filter prefix (`t ` -> `f ` -> `# ` -> none), Ctrl+C copies the selected path.
* Mouse: click selects, double-click/Enter navigates; wheel scrolls.
* Navigation: `navigationService.Navigate(item)`; on success `searchService.RecordUsage(item)` and close.
* Footer: result count, index status (`searchService.StatusText`), elapsed ms, keyboard hints.
* Empty query shows recent files then symbols (search service decides ordering).
* No exceptions escape into the VS dispatcher; failed navigation shows the error in the footer.
* `CtrlTNav.UiDemo` (net8.0-windows) must render the window with sample data to
  `artifacts/screenshots/search-window-dark.png` and `...-light.png` (offscreen
  `RenderTargetBitmap`, no interactive window required).

## 7. `CtrlTNav.Vsix` — required pieces

Namespace `CtrlTNav.Vsix`. net472, VSSDK package.

* `CtrlTNavPackage : AsyncPackage` — package GUID `{7cd65e41-7938-4826-a99a-0ec3b9bba331}`;
  `[ProvideMenuResource("Menus.ctmenu", 1)]`, `[ProvideAutoLoad(UIContextGuids80.SolutionExists,
  PackageAutoLoadFlags.BackgroundLoad)]`, `[ProvideOptionPage(typeof(CtrlTNavOptionsPage), "Ctrl+T
  Navigator", "General", 0, 0, true)]`, `[InstalledProductRegistration]`.
* `CtrlTNavConstants` — command set GUID `{deff788a-7d39-4399-aee3-e8bb272d4bd0}`, command ids
  `cmdidGoToEverything = 0x0100`, `cmdidGoToEverythingAlt = 0x0101`, options page GUID
  `{906af635-e1aa-4b52-b9d3-1f2584b60e13}`.
* `CtrlTNavPackage.vsct` — command table with the two buttons (Edit > Go To menu placement,
  best-effort), key bindings Ctrl+T and Ctrl+, (`editor="guidVSStd97"`).
* `Options/CtrlTNavOptionsPage.cs` — `DialogPage` mapping to `CtrlTNavOptions` (writes through on apply).
* `Options/OptionsProvider.cs` — thread-safe current options snapshot + `OptionsChanged` event.
* `Services/VsSearchService.cs` — `ISearchService` implementation; owns
  `Matching.SearchIndex`, `RecentUsageTracker`, and wires `ICppIndex.ItemsChanged` into the search
  index; builds `SearchOptions` from options + current project; records MRU and saves it (debounced).
* `Services/VsSolutionFileProvider.cs` — `ISolutionFileProvider` over EnvDTE (`Solution.Projects`,
  recursive `ProjectItems`, `FileProperties.FullPath`), filtered by extensions/excludes; null-safe.
* `Services/IndexCoordinator.cs` — orchestrates index build on solution open, status bar progress,
  re-index on document save (`IVsRunningDocTableEvents3`), `FileSystemWatcher` for external changes
  (debounce ~800 ms), index cache load/save via `Indexing.IndexCache` (optional but preferred),
  solution close/rename cleanup.
* `Navigation/VsNavigator.cs` — `INavigationService` via EnvDTE: `ItemOperations.OpenFile`,
  `TextSelection.GotoLine`, `Activate`; retries if document is not ready; returns false + message on fail.
* `Commands/GoToEverythingCommand.cs` — `OleMenuCommand`; when `OverrideCtrlT` is false execute the
  native `Edit.GoToAll`; alt command falls back to `Tools.Options`; otherwise show the window
  (pre-created on the UI thread during package load).
* `Theming/VsThemeProvider.cs` — reads VS environment colours
  (`Microsoft.VisualStudio.PlatformUI.VSColorTheme.GetThemedColor(EnvironmentColors...)`) and maps
  them to `SearchWindowTheme` (`ArgbColor`).
* `source.extension.vsixmanifest` — identity `CtrlTNav.GoToEverything`, version 1.0.0, installation
  targets VS Community/Pro/Enterprise `[17.0,19.0)` including `ProductArchitecture` amd64.
* Error handling: every entry point wrapped so a failure never crashes devenv; log via `ILog`
  (file log by default) and/or the VS activity log.

## 8. Build & packaging

* `build.ps1` (Lead) restores, builds the whole solution with the .NET SDK and runs the tests.
* `pack-vsix.ps1` (Lead) produces `artifacts/CtrlTNav.GoToEverything.vsix` from the built output:
  DLLs + `CtrlTNav.pkgdef` (generated with the VSSDK tools) + `extension.vsixmanifest`.
* The official `Microsoft.VSSDK.BuildTools` targets are used when they work with `dotnet build`;
  otherwise the packaging script assembles the VSIX container directly (documented in
  `docs/BUILD.md`). Either path must produce a valid container; verification steps are recorded in
  `docs/ACCEPTANCE.md`.

## 9. Conventions

* C# language features must compile for **net472** (C# 10 file-scoped namespaces are fine; no
  `record`, no `init`, no `required` because the runtime lacks the needed attributes; no
  `System.Index`/`Range`, no `Span` remoting assumptions).
* Nullable reference types enabled; treat warnings as informational (not errors).
* No NuGet packages in Core/Matching/Indexing/UI except `Microsoft.NETFramework.ReferenceAssemblies`
  from `Directory.Build.props`.
* Tests use xUnit (`tests/CtrlTNav.Tests`); test data lives under `tests/CtrlTNav.Tests/TestData/`.
* Every public type/member gets a short XML doc comment where behaviour is non-obvious.
* Performance-sensitive code avoids LINQ in inner loops, uses `StringComparison.Ordinal`,
  pre-computed lower-case strings, and structs/arrays for scratch data.
* Keep allocations out of the search hot path: no `string.ToLower()` per candidate (precompute),
  no `Substring` (use index ranges).
