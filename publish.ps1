# Builds a self-contained, single-file Windows release of Sky Session Claude.
# Output: dist/SkySessionClaude.exe (no .NET runtime required to run it).
param(
    [string]$Runtime = 'win-x64',
    [string]$OutDir  = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

dotnet publish "$root/src/SessionApp/SessionApp.csproj" `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o "$root/$OutDir"

Write-Host "Built: $root/$OutDir/SkySessionClaude.exe"
