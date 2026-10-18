# Build guide

## Prerequisites

| Tool | Version | Notes |
|------|---------|-------|
| Windows | 10 / 11 | Visual Studio extension host |
| .NET SDK | 8.0.x | `build.ps1` finds a local portable SDK first (`<repo>\.dotnet`), then `%USERPROFILE%\.dotnet`, then `dotnet` on `PATH` |
| PowerShell | 5.1+ or 7.x | Both scripts work on Windows PowerShell 5.1; PowerShell 7 is used automatically when installed |
| Visual Studio | 2022 17.x or 2026 (18.x), with the **Visual Studio extension development** workload | Only needed to install the VSIX / F5-debug it. The solution builds without Visual Studio. |
| .NET Framework 4.8 targeting pack | optional | The NuGet reference-assembly package is used automatically, so a full Visual Studio is not required to compile `net472` |

The repository is self-contained apart from NuGet restore. No Visual Studio installation is required
to *build*: `dotnet build` compiles every assembly, the test suite runs on the .NET 8 runtime, and
`pack-vsix.ps1` assembles the VSIX with the standalone VSSDK tools from NuGet (`vsct.exe`,
`VsixUtil.exe`, `CreatePkgDef.exe`).

## Commands

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1                          # restore + build + test (Debug)
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release   # release build + tests
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NoTest                  # build only
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Filter "FullyQualifiedName~FuzzyMatcher"

powershell -NoProfile -ExecutionPolicy Bypass -File .\pack-vsix.ps1                      # build (Debug) + package artifacts\CtrlTNav.GoToEverything.vsix
powershell -NoProfile -ExecutionPolicy Bypass -File .\pack-vsix.ps1 -Configuration Release
```

On a machine with PowerShell 7 installed, `pwsh -File .\build.ps1` works the same way; `build.ps1 -Pack`
automatically calls `pwsh` when available and falls back to `powershell.exe` otherwise.

The verified release build in this repository produced **0 errors** and **252/252 passing tests**; see
`docs/evidence/build-release.log`, `docs/evidence/test-results.log`, and `docs/evidence/pack-release.log`.

## What gets built

| Project | Target | Output |
|---------|--------|--------|
| `src/CtrlTNav.Core` | `net472;net8.0` | contracts, models, settings |
| `src/CtrlTNav.Matching` | `net472;net8.0` | fuzzy matcher, ranking, search index, MRU |
| `src/CtrlTNav.Indexing` | `net472;net8.0` | C++ extraction, `.sln`/`.vcxproj` scanning, index cache |
| `src/CtrlTNav.UI` | `net472;net8.0-windows` | WPF popup (programmatic UI, no XAML) |
| `src/CtrlTNav.Vsix` | `net472` | VSSDK package + VSIX |
| `tests/CtrlTNav.Tests` | `net8.0` | xUnit tests for matching + indexing |
| `tools/CtrlTNav.Smoke` | `net8.0` | end-to-end console harness |
| `tools/CtrlTNav.UiDemo` | `net8.0-windows` | renders the popup to `artifacts/screenshots/*.png` |

## Installing the extension

1. Build the VSIX: `powershell -NoProfile -ExecutionPolicy Bypass -File .\pack-vsix.ps1 -Configuration Release`.
2. Close Visual Studio.
3. Double-click `artifacts\CtrlTNav.GoToEverything.vsix` (or run
   `VSIXInstaller.exe artifacts\CtrlTNav.GoToEverything.vsix /quiet`) and pick the VS 2022/2026 instance.
4. Start Visual Studio, open a C/C++ solution, wait for the status bar message
   `Ctrl+T Navigator: <n> symbols from <m> files indexed`.
5. Press **Ctrl+T**.

The extension can be uninstalled through **Extensions > Manage Extensions**; it keeps only
`%LOCALAPPDATA%\CtrlTNav` (recent-usage file, log, optional index cache) which can be deleted safely.

## Troubleshooting

* **`NETSDK1045` / missing targeting pack** - the `Microsoft.NETFramework.ReferenceAssemblies` package in
  `Directory.Build.props` provides the `net472` reference assemblies; make sure NuGet restore succeeded.
* **Slow restore** - the first restore downloads the Visual Studio SDK build packages (~100 MB).
* **`VSCTCompile` / MSBuild task errors** - `pack-vsix.ps1` does not rely on the .NET Framework VSSDK
  MSBuild tasks; it runs `vsct.exe`, `VsixUtil.exe mergeCto` and `CreatePkgDef.exe` directly, then zips
  the container. It works with the `dotnet` CLI and with Windows PowerShell 5.1.
* **PowerShell execution policy** - use `-ExecutionPolicy Bypass` as shown above, or unblock the scripts
  (`Unblock-File build.ps1, pack-vsix.ps1`).
* **`System.IO.Compression.ZipArchiveMode` not found** - `pack-vsix.ps1` loads the
  `System.IO.Compression` and `System.IO.Compression.FileSystem` assemblies itself; do not remove those
  `Add-Type` lines when editing the script.
* **Nothing happens on Ctrl+T** - check **Tools > Options > Ctrl+T Navigator** for the shortcut options,
  and verify the key binding under **Tools > Options > Environment > Keyboard** (`CtrlTNav.GoToEverything`).
* **Logs** - `%LOCALAPPDATA%\CtrlTNav\ctrltnav.log`; Visual Studio also records package load issues in
  the ActivityLog (`devenv.exe /log`).
