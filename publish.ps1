# Builds a self-contained, single-file Windows release of Sky Session Claude.
# Output: dist/SkySessionClaude.exe (no .NET runtime required to run it).
#
# By default it then installs that exe into the stable location the "Sky Session
# Claude" Start-menu shortcut points at (%LOCALAPPDATA%\Programs\SkySessionClaude),
# so a release keeps the stable app up to date. Pass -SkipInstall to only fill dist.
param(
    [string]$Runtime = 'win-x64',
    [string]$OutDir  = 'dist',
    [switch]$SkipInstall
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

# Headless CLI (JSON output for the morning brief); shares SessionCore with the app.
dotnet publish "$root/src/SessionCli/SessionCli.csproj" `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o "$root/$OutDir"

Write-Host "Built: $root/$OutDir/SessionCli.exe"

# Refresh the stable install so the "Sky Session Claude" Start-menu shortcut (which
# points at %LOCALAPPDATA%\Programs\SkySessionClaude) runs the version we just built.
if (-not $SkipInstall) {
    $installDir = Join-Path $env:LOCALAPPDATA 'Programs\SkySessionClaude'
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null

    $targetExe = Join-Path $installDir 'SkySessionClaude.exe'
    # A running stable instance holds a lock on its exe; close only that one (never the
    # dev build) so the copy can overwrite it.
    #
    # There is one Sky window per desktop now (see SingleInstance), so a dev build left
    # running here will keep the stable app from starting afterwards. Launch the dev build
    # with --multi when you want to publish underneath it.
    $stable = @(Get-Process SkySessionClaude -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $targetExe })
    $wasRunning = $stable.Count -gt 0

    # Ask it to leave, rather than closing its window: closing only hides Sky to the tray
    # now, so CloseMainWindow would time out and we would be killing it -- which skips the
    # tidy-up and leaves its icon behind in the tray. --quit signals the instance that holds
    # the single-instance slot (see SingleInstance) and returns immediately.
    if ($wasRunning) {
        & $targetExe --quit | Out-Null
        foreach ($proc in $stable) { if (-not $proc.WaitForExit(5000)) { $proc.Kill() } }
    }

    Copy-Item "$root/$OutDir/SkySessionClaude.exe" $targetExe -Force
    Write-Host "Installed: $targetExe"

    # The CLI ships to the same stable place. It is the path the sky-session skill names,
    # so an agent that reads the skill finds the build that was last released rather than
    # whatever happens to be sitting in dist.
    $targetCli = Join-Path $installDir 'SessionCli.exe'
    Copy-Item "$root/$OutDir/SessionCli.exe" $targetCli -Force
    Write-Host "Installed: $targetCli"

    # Put back what we closed. A publish that leaves the desktop emptier than it found it
    # is a publish you have to remember to finish by hand — and only ever what was up:
    # starting an app nobody had open would be this script deciding something for you.
    if ($wasRunning) {
        Start-Process $targetExe
        Write-Host "Relaunched: $targetExe"
    }
} else {
    Write-Host "Skipped install (-SkipInstall); stable Start-menu app not updated."
}
