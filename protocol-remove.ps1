<#
.SYNOPSIS
    Removes the skysession:// URL handler that publish.ps1 registers.

.DESCRIPTION
    publish.ps1 writes HKCU:\Software\Classes\skysession when it refreshes the stable
    install, so a skysession:// link opens the app. This takes it back out.

    Worth having as its own script rather than a flag on publish: a handler left behind
    after the exe it points at is gone means a click on an old link fails in whatever way
    Windows chooses. Removing the app should remove this too.

.EXAMPLE
    .\protocol-remove.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$protocol = 'HKCU:\Software\Classes\skysession'

if (Test-Path $protocol) {
    Remove-Item -Path $protocol -Recurse -Force
    Write-Host "Removed the skysession:// handler" -ForegroundColor Green
} else {
    Write-Host "No skysession:// handler registered — nothing to do." -ForegroundColor DarkGray
}
