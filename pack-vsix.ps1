#requires -Version 5.1
<#
.SYNOPSIS
    Builds and packages the Ctrl+T Navigator for C++ Visual Studio extension (.vsix).

.DESCRIPTION
    With the .NET CLI, the VSSDK MSBuild tasks cannot run (they are .NET Framework only), so this script
    performs the packaging steps with the standalone VSSDK tools that ship in the
    Microsoft.VSSDK.BuildTools NuGet package:

      1. vsct.exe        compiles CtrlTNavPackage.vsct -> CtrlTNavPackage.cto
      2. VsixUtil.exe    merges the .cto into a managed .resources file as the "Menus.ctmenu" entry
                         (this is what MergeCtoResource does inside Visual Studio)
      3. dotnet build    compiles CtrlTNav.Vsix.dll with that .resources embedded
      4. CreatePkgDef.exe generates CtrlTNav.pkgdef from the package attributes
      5. the VSIX container is assembled as a zip:
             extension.vsixmanifest, CtrlTNav.pkgdef, CtrlTNav*.dll, Resources/*

    When built from inside Visual Studio (full MSBuild), the csproj imports the official VSSDK targets and
    produces the VSIX without this script.

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER SkipBuild
    Reuses the existing binaries (still recompiles CtrlTNav.Vsix.dll with the command table resource).

.PARAMETER OutputPath
    Optional output .vsix path. Defaults to artifacts\CtrlTNav.GoToEverything.vsix.

.EXAMPLE
    pwsh -File .\pack-vsix.ps1
    pwsh -File .\pack-vsix.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$SkipBuild,

    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$script:Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:BuildToolsVersion = '17.14.2142'

function Resolve-DotNet {
    $candidates = @(
        (Join-Path $script:Root '.dotnet\dotnet.exe'),
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
        (Join-Path $env:USERPROFILE '.dotnet-sdk\dotnet.exe'),
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1)
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($candidate in $candidates) {
        try {
            $version = & $candidate --version 2>$null
            if ($LASTEXITCODE -eq 0 -and $version) {
                return @{ Path = $candidate; Version = $version.Trim() }
            }
        }
        catch {
            # next
        }
    }

    throw 'No .NET SDK found. Install the .NET 8 SDK or place a portable SDK under <repo>/.dotnet.'
}

function Get-NuGetRoot {
    if ($env:NUGET_PACKAGES) {
        return $env:NUGET_PACKAGES
    }

    return Join-Path $env:USERPROFILE '.nuget\packages'
}

function Invoke-Tool {
    param([string]$FilePath, [string[]]$Arguments, [string]$FriendlyName)

    Write-Host "  > $FriendlyName" -ForegroundColor DarkGray
    Write-Host "    $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $FilePath @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$FriendlyName failed with exit code $LASTEXITCODE."
    }
}

$dotnet = Resolve-DotNet
$env:DOTNET_ROOT = Split-Path -Parent $dotnet.Path
$env:PATH = "$(Split-Path -Parent $dotnet.Path);$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host "Using .NET SDK $($dotnet.Version) ($($dotnet.Path))" -ForegroundColor Gray

$nugetRoot = Get-NuGetRoot
$buildTools = Join-Path $nugetRoot "microsoft.vssdk.buildtools\$($script:BuildToolsVersion)\tools\vssdk"
$vsctExe = Join-Path $buildTools 'bin\VSCT.exe'
$vsixUtilExe = Join-Path $buildTools 'bin\VsixUtil.exe'
$createPkgDefExe = Join-Path $buildTools 'bin\CreatePkgDef.exe'
if (-not (Test-Path $vsctExe) -or -not (Test-Path $vsixUtilExe) -or -not (Test-Path $createPkgDefExe)) {
    throw "VSSDK build tools were not found under '$buildTools'. Run 'pwsh -File .\build.ps1' (restore) first."
}

$sln = Join-Path $script:Root 'CtrlTNav.sln'
$artifactsDir = Join-Path $script:Root 'artifacts'
$stagingDir = Join-Path $artifactsDir "packaging\$Configuration"
$vsixProjectDir = Join-Path $script:Root 'src\CtrlTNav.Vsix'
$vsixProject = Join-Path $vsixProjectDir 'CtrlTNav.Vsix.csproj'
$binDir = Join-Path $vsixProjectDir "bin\$Configuration\net472"
$resourceDir = Join-Path $vsixProjectDir 'Resources'
$vsixPath = if ($OutputPath) { [System.IO.Path]::GetFullPath($OutputPath) } else { Join-Path $artifactsDir 'CtrlTNav.GoToEverything.vsix' }

if (-not (Test-Path $artifactsDir)) { New-Item -ItemType Directory -Path $artifactsDir | Out-Null }
if (-not (Test-Path $stagingDir)) { New-Item -ItemType Directory -Path $stagingDir | Out-Null }

# ---------------------------------------------------------------------------------------------
# 1. Compile the command table.
# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '==> Compile command table (vsct -> cto)' -ForegroundColor Cyan
$ctoPath = Join-Path $stagingDir 'CtrlTNavPackage.cto'
Remove-Item $ctoPath -Force -ErrorAction SilentlyContinue
Invoke-Tool -FilePath $vsctExe -FriendlyName 'VSCT compiler' -Arguments @(
    (Join-Path $vsixProjectDir 'CtrlTNavPackage.vsct'),
    $ctoPath,
    '-nologo',
    "-I$(Join-Path $buildTools 'inc')",
    "-I$(Join-Path $buildTools 'inc\office10')"
)
if (-not (Test-Path $ctoPath)) { throw 'vsct.exe did not produce a .cto file.' }

# ---------------------------------------------------------------------------------------------
# 2. Merge the .cto into a managed .resources file (the Menus.ctmenu resource).
# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '==> Merge command table into package resources' -ForegroundColor Cyan
$resourceFile = Join-Path $stagingDir '_EmptyResource.resources'
Remove-Item $resourceFile -Force -ErrorAction SilentlyContinue
Remove-Item "$resourceFile.original" -Force -ErrorAction SilentlyContinue

$writer = New-Object System.Resources.ResourceWriter($resourceFile)
$writer.Generate()
$writer.Close()

$ctoManifest = Join-Path $stagingDir 'ctoFiles.json'
$resourceManifest = Join-Path $stagingDir 'resources.json'
$ctoManifestData = @{
    CtoFiles = @(
        @{
            Path = $ctoPath
            Culture = ''
            ResourceName = 'Menus.ctmenu'
        }
    )
} | ConvertTo-Json -Depth 6
$resourceManifestData = @{
    Resources = @(
        @{
            Path = $resourceFile
            Culture = ''
        }
    )
} | ConvertTo-Json -Depth 6
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($ctoManifest, $ctoManifestData, $utf8NoBom)
[System.IO.File]::WriteAllText($resourceManifest, $resourceManifestData, $utf8NoBom)

Invoke-Tool -FilePath $vsixUtilExe -FriendlyName 'VsixUtil mergeCto' -Arguments @(
    'mergeCto',
    '-ctoFileManifest', $ctoManifest,
    '-resourceManifest', $resourceManifest
)
if (-not (Test-Path $resourceFile)) { throw 'mergeCto did not produce the merged .resources file.' }

# Sanity check: the merged resource file must contain the Menus.ctmenu entry.
$reader = New-Object System.Resources.ResourceReader($resourceFile)
$found = $false
try {
    $enumerator = $reader.GetEnumerator()
    while ($enumerator.MoveNext()) {
        if ([string]$enumerator.Key -eq 'Menus.ctmenu') { $found = $true }
    }
}
finally {
    $reader.Close()
}
if (-not $found) { throw "The merged resources file does not contain the 'Menus.ctmenu' entry." }
Write-Host '  merged resources contain Menus.ctmenu' -ForegroundColor DarkGray

# ---------------------------------------------------------------------------------------------
# 3. Build the solution with the command table resource embedded.
# ---------------------------------------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Host ''
    Write-Host "==> Restore and build solution ($Configuration)" -ForegroundColor Cyan
    Invoke-Tool -FilePath $dotnet.Path -FriendlyName 'dotnet restore' -Arguments @('restore', $sln)
    Invoke-Tool -FilePath $dotnet.Path -FriendlyName 'dotnet build' -Arguments @(
        'build', $sln,
        '--no-restore',
        '-c', $Configuration,
        '-v', 'minimal',
        '-p:CtrlTNavManualPackaging=true',
        "-p:VsixCtoResource=$resourceFile"
    )
}
else {
    # Rebuild only the package assembly so the command table resource is definitely embedded.
    Invoke-Tool -FilePath $dotnet.Path -FriendlyName 'dotnet build (Vsix only)' -Arguments @(
        'build', $vsixProject,
        '-c', $Configuration,
        '-v', 'minimal',
        '-p:CtrlTNavManualPackaging=true',
        "-p:VsixCtoResource=$resourceFile"
    )
}

$requiredAssemblies = @(
    'CtrlTNav.Vsix.dll',
    'CtrlTNav.Core.dll',
    'CtrlTNav.Matching.dll',
    'CtrlTNav.Indexing.dll',
    'CtrlTNav.UI.dll'
)
foreach ($assembly in $requiredAssemblies) {
    if (-not (Test-Path (Join-Path $binDir $assembly))) {
        throw "Expected build output '$assembly' was not found in '$binDir'."
    }
}

# ---------------------------------------------------------------------------------------------
# 4. Generate the pkgdef from the package attributes.
# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '==> Generate pkgdef (CreatePkgDef)' -ForegroundColor Cyan
$packageDll = Join-Path $binDir 'CtrlTNav.Vsix.dll'
$pkgdefPath = Join-Path $stagingDir 'CtrlTNav.pkgdef'
Remove-Item $pkgdefPath -Force -ErrorAction SilentlyContinue

# CreatePkgDef reflects over the package assembly and needs to resolve the Visual Studio SDK assemblies
# and our own dependencies; referencing every DLL next to the assembly works for both.
$referencedAssemblies = Get-ChildItem $binDir -Filter '*.dll' -File |
    Where-Object { $_.Name -ne 'CtrlTNav.Vsix.dll' } |
    Select-Object -ExpandProperty FullName
$includeArgument = '/i=' + ($referencedAssemblies -join ';')
Invoke-Tool -FilePath $createPkgDefExe -FriendlyName 'CreatePkgDef' -Arguments @(
    "/out=$pkgdefPath",
    '/codebase',
    $includeArgument,
    $packageDll
)
if (-not (Test-Path $pkgdefPath)) { throw 'CreatePkgDef did not produce a pkgdef file.' }

$pkgdefText = Get-Content $pkgdefPath -Raw
foreach ($needle in @('[$RootKey$\Packages\', '[$RootKey$\Menus', 'BindingPaths')) {
    if ($pkgdefText.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        Write-Warning "pkgdef does not contain the expected '$needle' section. Verify the package attributes."
    }
}

# ---------------------------------------------------------------------------------------------
# 5. Assemble the VSIX container.
# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '==> Assemble VSIX container' -ForegroundColor Cyan
$stagingVsix = Join-Path $stagingDir 'vsix'
if (Test-Path $stagingVsix) { Remove-Item $stagingVsix -Recurse -Force }
New-Item -ItemType Directory -Path $stagingVsix | Out-Null

Copy-Item (Join-Path $vsixProjectDir 'source.extension.vsixmanifest') (Join-Path $stagingVsix 'extension.vsixmanifest') -Force
Copy-Item $pkgdefPath (Join-Path $stagingVsix 'CtrlTNav.pkgdef') -Force
foreach ($assembly in $requiredAssemblies) {
    Copy-Item (Join-Path $binDir $assembly) (Join-Path $stagingVsix $assembly) -Force
}

$resourcesTarget = Join-Path $stagingVsix 'Resources'
New-Item -ItemType Directory -Path $resourcesTarget | Out-Null
Get-ChildItem $resourceDir -File | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $resourcesTarget $_.Name) -Force
}

if (Test-Path $vsixPath) { Remove-Item $vsixPath -Force }
$archive = [System.IO.Compression.ZipFile]::Open($vsixPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $stagingFull = (Resolve-Path $stagingVsix).Path
    Get-ChildItem $stagingVsix -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($stagingFull.Length + 1).Replace('\', '/')
        $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $_.LastWriteTime
        $stream = $entry.Open()
        try {
            $input = [System.IO.File]::OpenRead($_.FullName)
            try { $input.CopyTo($stream) } finally { $input.Dispose() }
        }
        finally {
            $stream.Dispose()
        }
    }
}
finally {
    $archive.Dispose()
}

# ---------------------------------------------------------------------------------------------
# 6. Validate the produced container.
# ---------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '==> Validate VSIX' -ForegroundColor Cyan
$validationArchive = [System.IO.Compression.ZipFile]::OpenRead($vsixPath)
try {
    $entryNames = ($validationArchive.Entries | ForEach-Object { $_.FullName }) | Sort-Object
    foreach ($entry in @('extension.vsixmanifest', 'CtrlTNav.pkgdef', 'CtrlTNav.Vsix.dll')) {
        if ($entryNames -notcontains $entry) { throw "VSIX is missing the required entry '$entry'." }
    }

    $manifestEntry = $validationArchive.GetEntry('extension.vsixmanifest')
    $manifestStream = $manifestEntry.Open()
    try {
        $document = New-Object System.Xml.XmlDocument
        $document.Load($manifestStream)
    }
    finally {
        $manifestStream.Dispose()
    }

    if (-not $document.DocumentElement -or $document.DocumentElement.LocalName -ne 'PackageManifest') {
        throw 'extension.vsixmanifest is not a PackageManifest document.'
    }

    Write-Host "  entries ($($entryNames.Count)):" -ForegroundColor DarkGray
    $entryNames | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
}
finally {
    $validationArchive.Dispose()
}

Write-Host ''
Write-Host "VSIX created: $vsixPath" -ForegroundColor Green
Write-Host ("Size: {0:N0} KB" -f ((Get-Item $vsixPath).Length / 1KB)) -ForegroundColor Green
