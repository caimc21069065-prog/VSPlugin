#requires -Version 5.1
<#
.SYNOPSIS
    Builds, tests and (optionally) packs the Ctrl+T Navigator for C++ Visual Studio extension.

.DESCRIPTION
    Restores NuGet packages, builds CtrlTNav.sln, runs the xUnit test suite and, with -Pack,
    produces artifacts/CtrlTNav.GoToEverything.vsix.

    The script locates a .NET 8 SDK in this order:
      1. <repo>/.dotnet/dotnet.exe (portable, checked in as a build dependency when present)
      2. %USERPROFILE%\.dotnet/dotnet.exe
      3. dotnet on PATH

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER SkipRestore
    Skips dotnet restore (faster incremental builds).

.PARAMETER NoTest
    Builds only, without running tests.

.PARAMETER Pack
    Produces the .vsix container in artifacts/.

.PARAMETER Filter
    Optional xUnit filter expression, for example "FullyQualifiedName~FuzzyMatcher".

.EXAMPLE
    pwsh -File build.ps1
    pwsh -File build.ps1 -Pack -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$SkipRestore,
    [switch]$NoTest,
    [switch]$Pack,
    [string]$Filter = ''
)

$ErrorActionPreference = 'Stop'
$script:Root = Split-Path -Parent $MyInvocation.MyCommand.Path

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
            # try next candidate
        }
    }

    throw "No .NET SDK found. Install the .NET 8 SDK (https://dot.net) or place a portable SDK under <repo>\.dotnet ."
}

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0) {
        throw "Step '$Name' failed with exit code $LASTEXITCODE."
    }
}

$dotnet = Resolve-DotNet
Write-Host "Using .NET SDK $($dotnet.Version) ($($dotnet.Path))" -ForegroundColor Gray
$env:DOTNET_ROOT = Split-Path -Parent $dotnet.Path
$env:PATH = "$(Split-Path -Parent $dotnet.Path);$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$solution = Join-Path $script:Root 'CtrlTNav.sln'
$env:CTRLTNAV_BUILD_DOTNET = $dotnet.Path

if (-not $SkipRestore) {
    Invoke-Step 'Restore packages' {
        & $dotnet.Path restore $solution
    }
}

Invoke-Step "Build solution ($Configuration)" {
    & $dotnet.Path build $solution --no-restore -c $Configuration -v minimal
}

if (-not $NoTest) {
    Invoke-Step 'Run tests' {
        $testProject = Join-Path $script:Root 'tests\CtrlTNav.Tests\CtrlTNav.Tests.csproj'
        $args = @('test', $testProject, '--no-build', '-c', $Configuration, '--logger', 'console;verbosity=normal')
        if ($Filter) { $args += @('--filter', $Filter) }
        & $dotnet.Path @args
    }
}

if ($Pack) {
    Invoke-Step 'Pack VSIX' {
        $packScript = Join-Path $script:Root 'pack-vsix.ps1'
        $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($pwsh) {
            & $pwsh.Source -NoProfile -File $packScript -Configuration $Configuration
        }
        else {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $packScript -Configuration $Configuration
        }
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green

