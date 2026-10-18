# Visual Studio integration notes (`CtrlTNav.Vsix`)

This document records how the Visual Studio integration is wired, what the packaging step must do with
it, and which behaviours were verified without a running Visual Studio instance. It complements
`docs/ARCHITECTURE.md` (the frozen contract) and `docs/BUILD.md` (build/pack commands).

## 1. Files in `src/CtrlTNav.Vsix`

| File | Purpose |
|------|---------|
| `CtrlTNavConstants.cs` | Package/command-set/options-page GUIDs, command ids, product strings, native command names. Single source of truth; mirrors the VSCT and the VSIX manifest. |
| `CtrlTNavPackage.cs` | `AsyncPackage`: registration attributes, service graph creation, command registration, pre-creation of the popup, option/theme plumbing, disposal. |
| `CtrlTNavPackage.vsct` | Command table: two commands, Edit > Go To + Tools menu placement, Ctrl+T and Ctrl+, key bindings. |
| `source.extension.vsixmanifest` | VSIX v3 manifest (identity, VS 17.0–19.0 amd64 installation targets, icon/licence, one `VsPackage` asset). |
| `Options/CtrlTNavOptionsPage.cs` | `DialogPage` exposing every `CtrlTNavOptions` property with category/display name/description/default value; raises the apply callback. |
| `Options/OptionsProvider.cs` | Thread-safe current options snapshot, `OptionsChanged` classification (index scope / recent usage / display). |
| `Services/VsDteUtilities.cs` | Defensive EnvDTE helpers (solution path, active project, native command execution). |
| `Services/VsSolutionFileProvider.cs` | `ISolutionFileProvider` over `Solution.Projects` + recursive `ProjectItems`, background filtering, `.sln` parser fallback, additional include directory scan. |
| `Services/VsSearchService.cs` | `ISearchService`: owns `Matching.SearchIndex` + `RecentUsageTracker`, applies `ICppIndex.ItemsChanged`, MRU recording + debounced save, footer status text. |
| `Services/IndexCoordinator.cs` | Solution open/close/rename/project-load → background rebuild; `IVsRunningDocTableEvents3.OnAfterSave` → incremental update; `FileSystemWatcher` (800 ms debounce) → external changes; status-bar progress; disposal. |
| `Navigation/VsNavigator.cs` | `INavigationService` via EnvDTE `ItemOperations.OpenFile`, `TextSelection.GotoLine` + `MoveToLineAndOffset`; never throws, reports failures via `errorMessage`. |
| `Commands/GoToEverythingCommand.cs` | `OleMenuCommand` handler for both ids, native `Edit.GoToAll` / `Tools.Options` fallbacks, dynamic menu captions. |
| `Theming/VsThemeProvider.cs` | Maps `EnvironmentColors` to `CtrlTNav.UI.SearchWindowTheme` (`ArgbColor`), falls back to `Dark()`/`Light()`, listens to `VSColorTheme.ThemeChanged`. |
| `Diagnostics/VsLog.cs` | `ILog` implementation: file log in `%LOCALAPPDATA%\CtrlTNav\ctrltnav.log` + `ActivityLog` mirroring. |
| `Resources/logo.png` | 128x128 PNG extension icon ("T" + magnifier). |
| `Resources/license.txt`, `Resources/getting-started.txt` | Shown by the VSIX installer / extension manager. |

The project intentionally has **no WPF compile references**: it only uses `SearchWindowController`,
`SearchWindowOptions`, `SearchWindowTheme` and `ArgbColor`, all of which are WPF-free types.

## 2. Package registration (drives `CreatePkgDef.exe`)

All registration comes from attributes on `CtrlTNavPackage`; nothing is hand-written in the pkgdef.

```csharp
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration(product, description, version)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(UIContextGuids80.NoSolution,   PackageAutoLoadFlags.BackgroundLoad)]
[ProvideOptionPage(typeof(CtrlTNavOptionsPage), "Ctrl+T Navigator", "General", 0, 0, true)]
[ProvideBindingPath]
[Guid("7cd65e41-7938-4826-a99a-0ec3b9bba331")]
public sealed class CtrlTNavPackage : AsyncPackage
```

Packaging requirements that follow from this:

1. `CtrlTNav.pkgdef` must be generated from the **built `CtrlTNav.Vsix.dll`** (with `CreatePkgDef.exe`)
   after the command table resource is embedded — the package assembly has to be complete first.
2. `[ProvideBindingPath]` is what makes VS resolve `CtrlTNav.Core/Matching/Indexing/UI.dll` from the
   extension folder. For that reason the manifest must declare exactly **one** asset
   (`Microsoft.VisualStudio.VsPackage` → `CtrlTNav.pkgdef`) and must **not** declare
   `Microsoft.VisualStudio.Assembly` assets; all five managed DLLs live side by side in the VSIX root.
3. The `Menus.ctmenu` resource (compiled from the VSCT) must be embedded as a managed resource named
   `Menus.ctmenu` **inside** the package assembly (see §3); `ProvideMenuResource("Menus.ctmenu", 1)`
   looks it up there.
4. `ProvideAutoLoad` produces the expected `[$RootKey$\Packages\{guid}]` auto-load entries; no other
   registry values are needed for the commands/keybindings (they are in the ctmenu resource).
5. The pkgdef must be placed at the VSIX root next to the DLLs, exactly as declared in the manifest.

### Expected pkgdef contents (sanity check for `pack-vsix.ps1`)

Generated from the attributes above, the file must contain (among the standard package keys):

* `[$RootKey$\Packages\{7cd65e41-...}]` with `Class`/`CodeBase`/`AllowsBackgroundLoading` and the
  installed-product strings;
* `[$RootKey$\Packages\{7cd65e41-...}\Menus]` style registration for `Menus.ctmenu` (name/version);
* the auto-load entries for the `SolutionExists` and `NoSolution` UI contexts;
* `[$RootKey$\ToolsOptionsPages\Ctrl+T Navigator\General]` with the options page GUID and package GUID;
* `[$RootKey$\BindingPaths\{7cd65e41-...}]` (from `[ProvideBindingPath]`), pointing at `$PackageFolder$`.

## 3. Command table (VSCT)

Verified with the real VSSDK compiler (`Microsoft.VSSDK.BuildTools 17.14.2142`,
`tools\vssdk\bin\VSCT.exe`): `VSCT.exe CtrlTNavPackage.vsct Menus.cto -nologo` reports
**0 errors / 0 warnings**.

* The table is fully self-contained (no `Extern` includes): all GUIDs and ids are declared inline.
* Package GUID `{7cd65e41-7938-4826-a99a-0ec3b9bba331}` is the `<Commands package="...">`.
* Command set `{deff788a-7d39-4399-aee3-e8bb272d4bd0}` owns:
  * `cmdidGoToEverything` = `0x0100` (Edit > Go To group; key `Ctrl+T`);
  * `cmdidGoToEverythingAlt` = `0x0101` (Tools menu; key `Ctrl+,`);
  * groups `0x1020` (Edit > Go To) and `0x1021` (Tools menu).
* Menu ids used from `vsshlids.h`: `IDG_VS_EDIT_GOTO = 0x012D`, `IDM_VS_MENU_TOOLS = 0x0085`;
  key-binding editor scope `guidVSStd97 = {5EFC7975-14BC-11CF-9B2B-00AA00573819}`.

### Merging the .cto into the package assembly

VS does **not** read the raw `.cto` file from the VSIX. The VSSDK build merges it into the managed
`.resources` file that the C# compiler embeds in `CtrlTNav.Vsix.dll`. This was reproduced manually and
verified with the shipped tools (`tools\vssdk\bin\VsixUtil.exe`):

```json
// ctoFiles.json
{ "CtoFiles": [ { "Path": "Menus.cto", "Culture": "", "ResourceName": "Menus.ctmenu" } ] }

// resourceManifest.json
{ "Resources": [ { "Path": "CtrlTNav.Vsix.Menus.resources", "Culture": "" } ] }
```

```
VsixUtil.exe mergeCto -ctoFileManifest ctoFiles.json -resourceManifest resourceManifest.json
```

The command succeeds (exit 0) and rewrites the `.resources` file with a `Menus.ctmenu` entry
(`Byte[]`, the compressed command table; a `.original` backup of the resource file is created).
That `.resources` file must then be embedded into `CtrlTNav.Vsix.dll` (the official MSBuild target does
this implicitly by merging into the resource that `ResGen` produced). Note that the resource entry
keeps the logical name `Menus.ctmenu`, so the `ProvideMenuResource("Menus.ctmenu", 1)` attribute works
unchanged. If the pack script cannot inject the merged `.resources` during the compile, the alternative
is to add it as an `EmbeddedResource` item in an injected MSBuild targets file before
`CreateManifestResourceNames`; without the embedded resource the menu commands have no captions and the
Ctrl+T / Ctrl+, key bindings are not registered.

## 4. Threading model

* `AsyncPackage.InitializeAsync` runs on a background thread (`BackgroundLoad`); it switches to the main
  thread before touching `DialogPage`, the menu command service, DTE or WPF (`SearchWindowController.Create`).
* The WPF popup is created (hidden) during package load so the first Ctrl+T is instant. If creation fails
  it is retried on the next command execution; failures never escape into the dispatcher.
* `ISearchService.Search` is called by the popup from a background task and never touches the DTE. The
  current project path is cached by the UI thread in `VsSearchService.UpdateCurrentProjectPath`.
* The search index has a single writer: `VsSearchService` applies `ICppIndex.ItemsChanged` under a
  private lock. Full rebuilds build a replacement index and swap the reference atomically so readers
  never observe a half-filled index.
* `IndexCoordinator` uses `fileSystemWatcher` + `System.Threading.Timer` debouncing; index operations run
  on thread-pool threads and are cancellable via `CancellationTokenSource`/`AsyncPackage.DisposalToken`.
* `VsNavigator.Navigate` can be called from any thread; it marshals through the package's
  `JoinableTaskFactory` and reports failures through `errorMessage` instead of throwing.

## 5. Behaviour summary

* Ctrl+T: if `OverrideCtrlT` is on (default) the pre-created popup is shown; otherwise
  `Edit.GoToAll` is executed natively, with a fallback to the popup if the native command fails.
* Ctrl+,: if `UseCtrlCommaShortcut` is on (default) the popup is shown; otherwise the native
  `Tools.Options` command is executed.
* Solution open/load/project load: debounced full rebuild (status bar shows progress and the final
  symbol/file counts).
* Document save: incremental `ICppIndex.UpdateFileAsync` for tracked files.
* External changes: `FileSystemWatcher` with an 800 ms debounce; modified/deleted tracked files are
  re-indexed/removed, watcher overflows schedule a full rebuild.
* Options change: index-scope changes trigger a debounced rebuild; display/recent-usage changes refresh
  the pre-created window.
* Theme change (`VSColorTheme.ThemeChanged`): the popup theme is rebuilt from `EnvironmentColors` and
  applied live (dark/light/blue themes).

## 6. Deliberate deviations

* **Index cache not wired.** `CtrlTNav.Indexing.IndexCache` is created by the indexer and covered by its
  tests, but it cannot be used by the integration layer: `IndexCache` has a private constructor
  (`Load` only, returns `null` when no cache exists) and `ICppIndex` exposes no API to seed a build from
  cached items or to skip unchanged files. Wiring it would require a contract change, so v1 rebuilds in
  memory on every solution open (which the background build and incremental updates make fast enough).
* **No submenu of its own.** Per the task, the commands are placed in the existing Edit > Go To group and
  the Tools menu instead of creating a dedicated top-level menu. `IDM_VS_MENU_EDIT = 0x0081` is available
  in the VSCT if a dedicated submenu is wanted later.
* **Menu icon.** The command table uses text-only buttons (no 16x16 bitmap strip) to keep the `.cto`
  merge step simple; the 128x128 `Resources/logo.png` is the extension icon.

## 7. Known risks / manual checks for Visual Studio

These need a real devenv instance (no Visual Studio is installed in the build environment):

1. Ctrl+T is also bound to the native `Edit.GoToAll`. The VSCT key binding is registered when the
   package loads; whether the extension binding wins over the built-in one is decided by VS. If VS keeps
   the native binding, the user can re-bind under **Tools > Options > Environment > Keyboard**
   (`CtrlTNav.GoToEverything`), or use Ctrl+, as the guaranteed alternative. `OverrideCtrlT = false`
   makes our command delegate to the native command, so behaviour stays correct either way.
2. Verify the menu items appear under **Edit > Go To** and **Tools** and that `Ctrl+,` executes the
   options command when the shortcut option is disabled.
3. Verify theme refresh when switching Visual Studio colour themes while the popup is open/closed.
4. For very large solutions the DTE project-item enumeration happens on the UI thread (recursive
   `ProjectItems`); it can take a moment on first load. The status bar shows "scanning solution files...".
5. Logs: `%LOCALAPPDATA%\CtrlTNav\ctrltnav.log`; package load problems also appear in the
   `devenv.exe /log` activity log with source `Ctrl+T Navigator for C++`.

## 8. Compatibility fixes applied during verification

* **`ProjectItem.FileProperties` is gone in the 17.14 interop package.** The `EnvDTE` facade in
  `Microsoft.VisualStudio.Interop 17.14.40260` type-forwards `ProjectItem` without the legacy
  `FileProperties` member. `VsSolutionFileProvider.TryGetPhysicalFilePath` now reads the same information
  from `ProjectItem.Properties` (`FullPath`) instead, which works with every project system that models
  items through DTE.
* **`IVsRunningDocTableEvents3` redeclares the base interface members.** The interop metadata uses `new`
  slots in `IVsRunningDocTableEvents2`/`3`, so explicit implementations of the base interface were not
  sufficient. `IndexCoordinator` implements all callbacks as public methods, which satisfies every
  interface layer; only `OnAfterSave` and `OnBeforeSave` have behaviour.
* **Packaging scripts are Windows PowerShell 5.1 compatible.** `pack-vsix.ps1` loads
  `System.IO.Compression(.FileSystem)` explicitly, writes the VsixUtil JSON manifests without a BOM, and
  `build.ps1 -Pack` falls back from `pwsh` to `powershell.exe`. The scripted packaging path was used to
  produce the verified VSIX; the official MSBuild/VSSDK path remains available when building inside
  Visual Studio.

