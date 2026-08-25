<#
.SYNOPSIS
    Removes the Scheduled Tasks registered by schedule-add.ps1.

.EXAMPLE
    .\schedule-remove.ps1
#>

[CmdletBinding()]
param(
    [string[]] $TaskName = @('kk-sessions-dump', 'kk-sessions-inbox')
)

$ErrorActionPreference = 'Stop'

foreach ($name in $TaskName) {
    if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $name -Confirm:$false
        Write-Host "Removed '$name'" -ForegroundColor Green
    } else {
        Write-Host "No task named '$name' found — nothing to do." -ForegroundColor DarkGray
    }
}
