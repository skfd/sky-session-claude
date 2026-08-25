<#
.SYNOPSIS
    Stops Sky Session Claude starting with Windows.

.DESCRIPTION
    publish.ps1 writes HKCU:\Software\Microsoft\Windows\CurrentVersion\Run when it refreshes
    the stable install, so Sky wakes with the machine and appears in the tray. This takes that
    entry out, along with the approval blob Windows keeps beside it.

    The blob is the reason this is a script rather than one line. Turning a startup app off in
    *Settings -> Apps -> Startup* does not delete the Run value; it writes a separate record
    under Explorer\StartupApproved\Run keyed by the same name. That asymmetry is deliberate on
    Windows' part and useful to us -- it is why re-publishing cannot switch autostart back on
    behind you -- but it means removing only the Run value leaves an orphaned "disabled"
    record behind, which quietly disables the entry again the day it is put back.

    Same shape as protocol-remove.ps1: uninstalling the app should be able to leave the
    registry as it found it.

.EXAMPLE
    .\startup-remove.ps1
#>

[CmdletBinding()]
param(
    [string] $Name = 'Sky Session Claude'
)

$ErrorActionPreference = 'Stop'

$run      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'

if (Get-ItemProperty -Path $run -Name $Name -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $run -Name $Name
    Write-Host "Removed '$Name' from the startup apps" -ForegroundColor Green
} else {
    Write-Host "'$Name' was not in the startup apps — nothing to do." -ForegroundColor DarkGray
}

# Only ever present once you have toggled the entry in Settings; absent is the normal case.
if (Get-ItemProperty -Path $approved -Name $Name -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $approved -Name $Name
    Write-Host "Removed the on/off record Settings kept for it" -ForegroundColor Green
}

Write-Host "Put it back with: .\publish.ps1" -ForegroundColor DarkGray
