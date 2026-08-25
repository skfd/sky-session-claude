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
    $wasRunning = @(Get-Process SkySessionClaude -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $targetExe }).Count -gt 0

    Get-Process SkySessionClaude -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $targetExe } |
        ForEach-Object { $_.CloseMainWindow() | Out-Null; if (-not $_.WaitForExit(5000)) { $_.Kill() } }

    Copy-Item "$root/$OutDir/SkySessionClaude.exe" $targetExe -Force
    Write-Host "Installed: $targetExe"

    # The CLI ships to the same stable place. It is the path the sky-session skill names,
    # so an agent that reads the skill finds the build that was last released rather than
    # whatever happens to be sitting in dist.
    $targetCli = Join-Path $installDir 'SessionCli.exe'
    Copy-Item "$root/$OutDir/SessionCli.exe" $targetCli -Force
    Write-Host "Installed: $targetCli"

    # Register skysession:// so a link in a local page, a note or a terminal opens a session.
    #
    # HKCU, not HKLM: this matches where the app installs (%LOCALAPPDATA%), needs no admin,
    # and keeps the handler to the account that asked for it. The command is "%1" quoted --
    # everything after the scheme is data, re-validated in-process by SessionUri, and never
    # concatenated into a shell. See docs/URI.md for the eight rules that keep it boring.
    $protocol = 'HKCU:\Software\Classes\skysession'
    New-Item -Path "$protocol\shell\open\command" -Force | Out-Null
    New-Item -Path "$protocol\DefaultIcon" -Force | Out-Null
    Set-ItemProperty -Path $protocol -Name '(Default)'   -Value 'URL:Sky Session Claude'
    # The presence of this value is what makes Windows treat the key as a URL scheme at all;
    # its content is ignored and is empty by convention.
    Set-ItemProperty -Path $protocol -Name 'URL Protocol' -Value ''
    Set-ItemProperty -Path "$protocol\DefaultIcon"     -Name '(Default)' -Value "$targetExe,0"
    Set-ItemProperty -Path "$protocol\shell\open\command" -Name '(Default)' -Value "`"$targetExe`" `"%1`""
    Write-Host "Registered: skysession:// -> $targetExe"

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
