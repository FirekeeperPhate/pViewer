# Builds the two distributable flavours of pViewer into .\artifacts
#   portable : single self-contained exe, runs without installing .NET
#   light    : small framework-dependent build, needs the .NET 10 Desktop Runtime
param([switch]$SkipTests)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test tests\pViewer.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

Remove-Item artifacts -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish src\pViewer\pViewer.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o artifacts\portable
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed" }

dotnet publish src\pViewer\pViewer.csproj -c Release -r win-x64 --self-contained false -o artifacts\light
if ($LASTEXITCODE -ne 0) { throw "Light publish failed" }

Remove-Item artifacts\portable\*.pdb, artifacts\light\*.pdb -ErrorAction SilentlyContinue
Get-ChildItem artifacts -Recurse -Filter pViewer.exe | ForEach-Object { "{0}  {1:N1} MB" -f $_.FullName, ($_.Length / 1MB) }
