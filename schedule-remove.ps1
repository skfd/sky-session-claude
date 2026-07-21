<#
.SYNOPSIS
    Removes the Scheduled Task registered by schedule-add.ps1.

.EXAMPLE
    .\schedule-remove.ps1
#>

[CmdletBinding()]
param(
    [string] $TaskName = 'kk-sessions-dump'
)

$ErrorActionPreference = 'Stop'

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Removed '$TaskName'" -ForegroundColor Green
} else {
    Write-Host "No task named '$TaskName' found — nothing to do." -ForegroundColor DarkGray
}
