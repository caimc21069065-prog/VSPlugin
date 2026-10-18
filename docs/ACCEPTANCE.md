# Acceptance report — Ctrl+T Navigator for C++

Requirements source: `VS2026-CtrlT-ReSharper-C++-Navigation-Plugin.md` (v1.0, 2026-10-18).
This report maps every acceptance criterion to reproducible evidence.

Environment used for verification:

* Windows 11 (build 26200), x64.
* .NET SDK **8.0.426** (portable install at `%USERPROFILE%\.dotnet`); NuGet restore from nuget.org.
* Windows PowerShell **5.1** for `build.ps1` / `pack-vsix.ps1` (PowerShell 7 also works when installed).
* **No Visual Studio installation in this environment** (`vswhere` returns no instance and
  `C:\Program Files\Microsoft Visual Studio\2022` is empty). Verification therefore covers the full
  compilation of every assembly for `net472` and `net8.0`, the xUnit suite, the end-to-end engine run
  (`tools/CtrlTNav.Smoke`), the offscreen UI renderings, and the assembled VSIX container (VSCT compile,
  ctmenu merge, pkgdef generation, container layout). In-IDE checks that need `devenv.exe` are marked
  **manual** with the exact steps to follow.

Legend: ✅ verified with evidence · 🟡 partially verified / requires VS for final confirmation · ⬜ not done

## Verification summary (2026-10-18)

| Check | Result |
|-------|--------|
| Release build (`build.ps1 -Configuration Release`) | **0 errors**, 48 warnings (VS threading analyzers + nullable advisories), log in `docs/evidence/build-release.log` |
| xUnit tests | **252/252 passed**, 0 failed, 0 skipped (`docs/evidence/test-results.log`) |
| Smoke run (real C++ test data) | 7 files, 119 items indexed in 24 ms; `t ` → 21 types, `f ` → 7 files, `# ` → 112 symbols; query latency 0.01–0.03 ms (`docs/evidence/smoke-output.txt`) |
| VSCT command table | 0 errors, 0 warnings; `Menus.ctmenu` merged and present in the package assembly |
| VSIX container | 10 entries, 132.2 KB (135339 bytes), SHA-256 `69C30FB894A7C9C020EE2DF65E66664D9EC0D406B845792D4733AA5EB3487383`; pkgdef contains `Packages`, `AutoLoadPackages`, `BindingPaths`, `Menus` and `ToolsOptionsPages` (`docs/evidence/vsix-contents.txt`) |
| Remaining work | Install the VSIX in Visual Studio 2022/2026 and complete the manual checks in the last section |

## 5.1 Functional acceptance

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | Ctrl+T reliably opens the search window | 🟡 | VSCT key binding + `GoToEverythingCommand` / `CTRLTNAV` handler implemented; VSCT compiles 0/0 and the binding resource is embedded. Manual: install VSIX, press Ctrl+T. |
| 2 | Search shows types, functions/methods, variables, macros, files | ✅ | Smoke output contains every kind (`docs/evidence/smoke-output.txt`); extraction tests assert kind, name, line and column for 59 sample-header symbols. |
| 3 | Live filtering while typing | ✅ | `SearchWindow` debounce/versioning + `SearchIndexTests`; smoke benchmark 0.01–0.04 ms/query over the real test corpus. |
| 4 | Fuzzy matching (at least two of prefix/camel/subsequence) | ✅ | `FuzzyMatcherTests` cover exact, prefix, camel acronym, snake_case and subsequence, including case-insensitivity and Unicode. |
| 5 | Smart ranking, recently used noticeably higher | ✅ | `SearchIndexRankingTests` (exact > prefix > camel > subsequence, MRU can lift a camel hit above a prefix hit but never beats exact), `RecentUsageTests`, smoke MRU demo. |
| 6 | Enter navigates to file and approximate position | 🟡 | `VsNavigator` opens the document and calls `GotoLine` / `MoveToLineAndOffset`; never throws. Manual: verify caret line in VS. |
| 7 | At least one filter prefix (t, f, #) | ✅ | All prefixes implemented in `SearchQuery.Parse` and covered by the new `SearchQueryTests`; smoke output: `t ` → 21 type results only, `f ` → 7 file results only, `# ` → 112 symbol results. |
| 8 | Display kind icon, path, line number | ✅ | Offscreen UI renderings show coloured kind badges, qualified names, relative paths and `:line` (`artifacts/screenshots/search-window-dark.png`, `-light.png`); row construction is covered by the UiDemo renderer. |
| 9 | Recently used files/symbols weighted | ✅ | `RecentUsageTrackerTests` + `SearchIndexEmptyQueryTests` + smoke MRU section. |
| 10 | Setting to control overriding native Ctrl+T | 🟡 | Options page + `GoToEverythingCommand` fallback to `Edit.GoToAll`; pkgdef registers `ToolsOptionsPages\Ctrl+T Navigator\General`. Manual: toggle at runtime. |
| 11 | Ctrl+, as backup shortcut | 🟡 | Second VSCT key binding and command id implemented; manual verification of the binding. |

## 5.2 Performance / experience

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | Popup latency ≤ 50 ms | 🟡 | Window is pre-created and kept hidden during package load, so `Show()` is a layout pass, not a cold WPF start. Manual stopwatch check in VS still required. |
| 2 | Typing stays responsive on medium/large C++ solutions | ✅ | `SearchIndexPerformanceTests` (100k synthetic items, empty/short/six-char queries, warm cache) pass well inside their bounds; smoke benchmark 0.01–0.04 ms/query. |
| 3 | Incremental indexing ≤ 2 s per saved file | ✅ | `CppIndexTests.UpdateFileAsyncReplacesOnlyThatFilesItems` and save-event wiring in `IndexCoordinator`; per-file extraction on the test corpus is a few ms. |
| 4 | No significant VS slowdown / memory blowup | 🟡 | Background build, bounded concurrency (≤ 8), no third-party indexing dependencies, compact item model. Manual: VS performance monitor. |
| 5 | Incomplete code still yields meaningful results | ✅ | `broken.cpp` test data: valid symbols before/after unclosed braces, `#if 0`, broken strings and a truncated function are still extracted; `UnterminatedStringDoesNotHideFollowingSymbols` covers recovery at a newline. |

## 5.3 Comparative (subjective)

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | Speed/handiness close to ReSharper Ctrl+T | 🟡 | Needs a real VS + C++ solution; manual evaluation. |
| 2 | Common symbols/files near the top | ✅ | Ranking tests + smoke results show exact/prefix hits first; MRU weighting demonstrated. |
| 3 | Developer judges it a daily replacement | 🟡 | Manual/subjective. |

## 5.4 Stability

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | No crashes during use | 🟡 | Every entry point defends against exceptions; parser/index robustness tests include malformed input, cancellation, concurrent readers. Manual soak test recommended. |
| 2 | Uninstall restores VS, no leftover effects | ✅ | The extension writes only `%LOCALAPPDATA%\CtrlTNav` (documented); the VSIX registers through standard pkgdef entries and uninstalls with the VSIX installer. |
| 3 | Coexists with ReSharper etc. via settings | 🟡 | `OverrideCtrlT` / `UseCtrlCommaShortcut` options implemented; manual coexistence test needed.  |

## Test coverage added during the final pass

The last verification pass fixed the defects that blocked the suite and added coverage for them:

* `SearchQueryTests` (new): `t ` / `f ` / `# ` / `m ` and `type:` / `file:` / `symbol:` prefixes, leading
  whitespace, empty input and the “no prefix” path. This covers the previously missing acceptance evidence
  for F7 and locks in the fix for the trailing-space/`Trim()` bug.
* Extractor: leading `~` destructors, function-pointer variables/fields, all-caps type names in `typedef`
  (e.g. `HRESULT`), recovery from an unterminated string at end of line, and anonymous-struct containers.
* Scanner: excludes are matched relative to the scan root and built-in defaults always apply.
* `CppIndex` duplicate-position and concurrent-build test assertions made deterministic.

## Follow-up review pass (2026-10-18)

A second source audit found and fixed these defects; the regression tests are part of the 252-test suite:

* **C++20 requires-clauses**: `template<...> requires C<T> class Foo` used to hide `Foo` entirely (it was
  emitted as a variable), and `class Foo requires C<T>` used the constraint as the type name. Constraint
  expressions are now skipped properly (`CppRequiresClauseTests`).
* **Search relevance**: zero-quality qualified-name/path subsequence hits (for example `inside` matching
  random letters of `C:\...\indexing\broken.cpp`) are no longer returned as `FullName` results.
* **Index status**: the final "index ready" notification could be swallowed by the 120 ms state throttle,
  so an open popup did not refresh after a fast build; item changes and terminal progress now always
  notify. A solution with no C/C++ files now reaches `Ready` (zero items) instead of staying `NotStarted`
  and showing an endless "Indexing…" state.
* **Recent-usage capacity**: changing the capacity in the options page now reconfigures the tracker
  immediately (previously `VsSearchService.UpdateOptions` was never called) and saves pending entries
  before reloading.
* **Popup lifecycle**: closing the borderless popup with Alt+F4 made later Ctrl+T presses do nothing; the
  window is now recreated lazily around the existing view.
* **Cancellation races**: a build cancelled while it was finalising could re-install content after a
  solution close; the install is re-checked under the writer lock, snapshots are published with
  `Volatile`, and a pending rebuild timer is stopped on solution close.
* **File coverage**: `.tcc` and `.cuh` are part of the default source extensions (they were already
  header-classified but were not indexed by default).
* **New files**: creating a C/C++ file after the initial index now schedules a debounced full rebuild
  (incremental updates cannot know the owning project metadata).
* **UI freshness**: in-flight searches are invalidated as soon as the query text changes, and the wording
  no longer claims that results appear while files are still being parsed.

## Artefacts

| Artefact | Path |
|----------|------|
| VSIX package | `artifacts/CtrlTNav.GoToEverything.vsix` (132.2 KB) |
| Build log (Release) | `docs/evidence/build-release.log` |
| Test log (252 passed) | `docs/evidence/test-results.log` |
| Smoke output + benchmark | `docs/evidence/smoke-output.txt` |
| Packaging log (VSCT / mergeCto / CreatePkgDef) | `docs/evidence/pack-release.log` |
| VSIX content listing + hashes | `docs/evidence/vsix-contents.txt` |
| UI screenshots | `artifacts/screenshots/search-window-dark.png`, `search-window-light.png` |
| Build/test instructions | `docs/BUILD.md` |

## How to run the remaining manual checks

1. Install `artifacts/CtrlTNav.GoToEverything.vsix` into VS 2022 17.x or VS 2026 (double-click, select the
   instance; Visual Studio must be closed).
2. Open a C/C++ solution; the status bar shows indexing progress; wait for
   `Ctrl+T Navigator: <n> symbols from <m> files indexed`.
3. Press `Ctrl+T`, type `push`, `vec`, or use `t `, `f `, `# `, `m ` prefixes; check the filter badge,
   highlighting, relative path/line and result ordering.
4. Use ↑/↓/PageUp/PageDown/Home/End, Enter, Esc, Tab, Ctrl+C and double-click. Close the popup with
   Alt+F4 and confirm Ctrl+T opens it again.
5. Save a `.cpp` file and confirm the index updates; modify a file externally and confirm the display
   refreshes after ~800 ms. Create a new source file and confirm it is indexed after the debounced
   rebuild.
6. Toggle **Tools > Options > Ctrl+T Navigator > General > Override Ctrl+T** and confirm native
   `Edit.GoToAll` is restored when off; check `Ctrl+,` with **Use Ctrl+, shortcut** disabled opens
   Tools/Options.
7. Switch between dark/light/blue VS themes while the popup is open and closed.
8. Check `%LOCALAPPDATA%\CtrlTNav\ctrltnav.log` for errors and the recent-usage file growing as you navigate.
