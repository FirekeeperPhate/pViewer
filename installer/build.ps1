# Builds the pViewer installers (Inno Setup).
#   .\build.ps1                -> Light and Full
#   .\build.ps1 -Flavor Full   -> Full only
#   .\build.ps1 -SkipTests     -> skip the unit tests
# Light = framework-dependent (needs the .NET 10 Desktop Runtime), Full = self-contained (runtime included).
# Output: installer\Output\pViewer-Setup-<version>-<Light|Full>.exe

param(
    [ValidateSet('Light', 'Full', 'All')]
    [string]$Flavor = 'All',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\pViewer\pViewer.csproj'

$iscc = @(
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'ISCC.exe (Inno Setup) not found.' }

$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Version not found in pViewer.csproj.' }

if (-not $SkipTests) {
    Write-Host '== Tests' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'tests\pViewer.Tests') -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

$flavors = if ($Flavor -eq 'All') { @('Light', 'Full') } else { @($Flavor) }

foreach ($f in $flavors) {
    $out = Join-Path $root ('publish\' + $f.ToLower())
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }   # no leftovers from previous builds
    $selfContained = if ($f -eq 'Full') { 'true' } else { 'false' }

    Write-Host "== Publish $f $version (self-contained: $selfContained)" -ForegroundColor Cyan
    dotnet publish $project -c Release -r win-x64 --self-contained $selfContained -o $out -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($f) failed." }

    Write-Host "== Installer $f" -ForegroundColor Cyan
    & $iscc /Q "/DFlavor=$f" "/DAppVersion=$version" (Join-Path $PSScriptRoot 'pViewer.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC ($f) failed." }
}

Get-ChildItem (Join-Path $PSScriptRoot 'Output') -Filter "pViewer-Setup-$version-*.exe" |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
