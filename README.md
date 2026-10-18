# Ctrl+T Navigator for C++ (Visual Studio extension)

A free, fast **Go to Everything** navigation popup for C/C++ in Visual Studio 2022 / 2026, modelled on
ReSharper C++'s `Ctrl+T`: one search box for types, functions, methods, variables, macros and files,
with fuzzy matching, smart ranking and recently-used weighting.

> Status: implementation complete and reviewed, **252/252 unit tests passing**, release VSIX packaged at
> `artifacts/CtrlTNav.GoToEverything.vsix`. The engine, indexing, UI rendering and VSIX container are
> verified on this machine; the in-IDE checks (Ctrl+T key binding, navigation caret, options toggles)
> still require a machine with Visual Studio installed — see `docs/ACCEPTANCE.md` for the evidence and
> the exact manual checklist.

## Highlights

* **Ctrl+T** anywhere in Visual Studio opens a light popup; type to filter instantly, `Enter` jumps to
  the file and line, `Esc` closes. `Ctrl+,` works as a backup shortcut.
* **Unified search** across C/C++ files discovered from the solution (`.sln` / `.vcxproj`):
  classes, structs, enums, namespaces, functions, methods, constructors/destructors/operators, fields,
  globals, typedefs/aliases, concepts and `#define` macros.
* **C++20 aware**: `requires`-clauses on classes and functions are skipped correctly instead of hiding
  the declaration they constrain, and off-by-default modules/imports are tolerated.
* **Fuzzy matching** with the ranking order ReSharper users expect:
  exact match → prefix → camel-case/underscore tokens → subsequence, plus recent-usage and
  current-project boosts.
* **Filter prefixes** - `t ` types, `f ` files, `# ` symbols (also `m ` members, `sym:`, `type:`, `file:`).
* **Background indexing** of the whole solution with incremental re-indexing on save and on external
  file changes; the popup stays responsive while the index is still building.
* **Options page** (**Tools > Options > Ctrl+T Navigator**): override the native Ctrl+T, enable Ctrl+,
  exclude globs, max results, recent-usage weighting, and more.
* Written in C# with a hand-written tolerant C++ tokenizer (no clangd/tree-sitter dependency), so it
  copes with incomplete code and does not need a build of the C++ project to index it.

## Repository layout

```
src/CtrlTNav.Core        contracts, models, settings (net472 + net8.0)
src/CtrlTNav.Matching    fuzzy matching, ranking, search index, MRU (net472 + net8.0)
src/CtrlTNav.Indexing    C/C++ symbol extraction, .sln/.vcxproj scanning, cache (net472 + net8.0)
src/CtrlTNav.UI          WPF popup window (net472 + net8.0-windows, programmatic UI)
src/CtrlTNav.Vsix        VSSDK package, commands, options page, VS adapters (net472)
tests/CtrlTNav.Tests     xUnit tests for matching + indexing
tools/CtrlTNav.Smoke     console end-to-end harness (index a folder, run queries, report timings)
tools/CtrlTNav.UiDemo    renders the popup to PNG for documentation/screenshots
docs/                    architecture, build guide, acceptance report
```

## Build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1      # restore + build + test
powershell -NoProfile -ExecutionPolicy Bypass -File .\pack-vsix.ps1  # produce artifacts/CtrlTNav.GoToEverything.vsix
```

Both scripts also run under PowerShell 7 (`pwsh`). Building requires only the .NET 8 SDK; Visual Studio is
needed only to install and use the extension. See `docs/BUILD.md` for prerequisites, VSIX installation and
troubleshooting.

## Usage

1. Install the VSIX and open a C/C++ solution.
2. Press **Ctrl+T**. Type `push` to find `push_back`, `PushBack`, `PushBackRange`, ...
3. Use `t `, `f ` or `# ` to restrict results to types, files or symbols.
4. `Enter` / double-click navigates, `Ctrl+C` copies the selected path, `Esc` closes.

## License

MIT. See `LICENSE`.
