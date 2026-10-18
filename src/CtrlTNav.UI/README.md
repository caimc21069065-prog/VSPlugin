# CtrlTNav.UI — the Ctrl+T search popup

Programmatic WPF implementation of the ReSharper-style **Go to Everything** popup. There is no XAML in this
assembly: the net472 flavour must build with the plain .NET SDK (no WPF build tasks), so every visual is
created in C#.

The assembly multi-targets `net472` (consumed by the VSIX) and `net8.0-windows` (used by the demo and the
screenshot renderer).

## Public surface (consumed by `CtrlTNav.Vsix`)

Nothing in the public API references a WPF type, so the VSIX assembly stays free of WPF references.

```csharp
SearchWindowController.Create(ISearchService, INavigationService, SearchWindowTheme, SearchWindowOptions);
controller.IsVisible;
controller.ShowSearch(string? presetText = null);
controller.HideSearch();
controller.UpdateOptions(SearchWindowOptions options);
controller.ApplyTheme(SearchWindowTheme theme);
controller.Dispose();
```

`ShowSearch` may be called from the VS UI thread; the controller marshals to its dispatcher when necessary.
`Create` must run on a WPF dispatcher (STA) thread — it builds the window and the full visual tree eagerly so
showing the popup is a single `Show()` (spec NF1).

### `SearchWindowTheme`

Colours are plain `ArgbColor` values; the VS integration maps `VSColorTheme` environment colours onto them:

| Property | Used for |
| --- | --- |
| `Background` | popup surface |
| `Foreground` | result names, search text |
| `SubtleForeground` | paths, placeholder, footer hints |
| `Border` | outline, separators |
| `SelectionBackground` / `SelectionForeground` | selected row |
| `Accent` | matched characters, carets, progress strip, `ALL` filter badge |
| `InputBackground` | the pill-shaped search field |
| `BadgeForeground` | neutral kind badges (files/unknown) |
| `CountForeground` | result counter and timing |

`SearchWindowTheme.Dark()` / `.Light()` are the built-in fallback palettes; `Clone()` returns a deep copy.
Kind colours (types = teal, callables = yellow, variables = light blue, macros = orange, files = grey, …) are
derived from the theme luminance inside the UI assembly, so a correct theme automatically gets correctly
tinted badges.

### `SearchWindowOptions`

`RelativePathBaseDirectory` (solution directory for relative paths), `ShowLineNumbers`, `ShowRelativePaths`,
`CloseOnFocusLost`, `Placeholder`.

## Layout

```
+--------------------------------------------------------- 720 DIP, corner radius 10
| header   [ALL]  Search types, symbols and files…   18 results
| ------------------------------------------------------------
| results  (virtualized rows, max height 420 DIP)
|    [C]  VulkanDevice                        Engine\…\VulkanDevice.h:18
|    [S]  DeviceInfo                          Engine\…\DeviceInfo.h:36
|    ...
| ------------------------------------------------------------
| footer   12,438 symbols · 96 files · Ready   ↑↓ navigate  Enter open  Tab filter  Esc close
+---------------------------------------------------------
```

* Borderless, transparent window with a drop shadow; horizontally centred and ~12 % from the top of the work
  area (owned by devenv so it stays above Visual Studio, without being topmost over other apps).
* Header: a pill-shaped input with the filter badge (`ALL`, `TYPES`, `FILES`, `SYMBOLS`, `MEMBERS`) on the
  left, a 19 pt search box, and the result counter on the right. The badge follows the prefix parsed by
  `SearchQuery`, so typing `t ` turns it green-ish (`TYPES`) immediately.
* Rows: coloured kind badge (`ItemKind.GetShortCode()`), name with the fuzzy-matched characters bolded and
  tinted with the accent colour, right-aligned relative path and `:line`.
* Footer: result count, elapsed search time, `ISearchService.StatusText`, and navigation errors. While the
  index is building a 2 px accent strip pulses under the header.
* Selection is drawn by the row element itself (accent background, readable foreground, white-on-blue badge);
  hover uses a subtle lightened background. Empty states (no matches / index still building) show a centred
  glyph with an explanation.

## Keyboard map

| Key | Action |
| --- | --- |
| type | search, 15 ms debounce on a background thread |
| `Up` / `Down` | move selection |
| `PageUp` / `PageDown` | move one page of visible rows |
| `Home` / `End` | first / last result |
| `Enter` | navigate to the selected result, record MRU usage, close |
| `Esc` | close (also closes on focus loss when `CloseOnFocusLost`) |
| `Tab` / `Shift+Tab` | cycle filter prefix `(none) → t → f → #`, preserving the query text |
| `Ctrl+C` | copy the selected result’s `path:line` |
| mouse | click selects, double-click navigates, wheel scrolls |

Filter prefixes are parsed by `CtrlTNav.Core.Search.SearchQuery`, which also supports `t:`, `type:`, `file:`,
`sym:`, `member:` and `m `.

## Search pipeline

* Text changes restart a 15 ms `DispatcherTimer`; the query runs on the thread pool through
  `ISearchService.Search` so typing never blocks the dispatcher.
* A monotonically increasing version counter discards results that belong to an older query (stale-result
  protection).
* `ISearchService.IndexStateChanged` is marshalled to the dispatcher; the footer text, progress strip and
  empty state refresh, and once the index becomes ready the current query is re-run so late symbols appear.
* Navigation never throws: failures are shown in the footer in the error colour, successes call
  `INavigationService.Navigate`, then `ISearchService.RecordUsage`, then hide the popup. Every event handler
  is guarded so no exception can escape into the Visual Studio dispatcher.

## Regenerating the screenshots

The demo tool (`tools/CtrlTNav.UiDemo`) hosts fake services over a realistic sample C++ solution and can
render the real popup visual tree offscreen (no window, no HWND: a full Measure/Arrange pass followed by
`RenderTargetBitmap` at 2× scale):

```powershell
# from the repository root
dotnet run --project tools/CtrlTNav.UiDemo -- --render artifacts/screenshots

# other query / theme data is easy to try too
dotnet run --project tools/CtrlTNav.UiDemo -- --render artifacts/screenshots --query "vk"
```

This writes `artifacts/screenshots/search-window-dark.png` and `search-window-light.png` (light/dark
backdrop, selected first row). Without `--render` the tool opens a small shell window; press `Ctrl+T` to
interact with the real popup.

`InternalsVisibleTo("CtrlTNav.UiDemo")` gives the renderer access to `SearchPopupView` and
`ApplyPreviewQuery`, which runs the search synchronously so a single layout pass is enough for the PNG.
