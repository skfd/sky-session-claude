<#
.SYNOPSIS
    Registers a Scheduled Task that refreshes sessions.json for the morning brief.

.DESCRIPTION
    The morning brief runs in a Linux sandbox that can only see mounted folders.
    ~/.claude/projects is a protected location and cannot be mounted, so the brief
    cannot enumerate Claude Code sessions on its own.

    This task runs SessionCli.exe --json on the host and drops the result
    into sessions.json next to this script, which the brief CAN read.

    Same pattern as ontario-address-changes\schedule-add.ps1.

.PARAMETER Time
    Daily run time, HH:mm. Default 06:45 — ahead of the 7am brief.

.EXAMPLE
    .\schedule-add.ps1
    .\schedule-add.ps1 -Time 06:15
#>

[CmdletBinding()]
param(
    [string] $Time     = '06:45',
    [string] $TaskName = 'Claude - dump sessions for morning brief'
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe  = Join-Path $here 'dist\SessionCli.exe'
$out  = Join-Path $here 'sessions.json'

if (-not (Test-Path $exe)) { throw "Cannot find $exe - run .\publish.ps1 first" }

# SessionCli scans all sessions by default, so a project with several stalled
# sessions shows each one, not just the newest.
$args = '--json "{0}" --top 60' -f $out

$action    = New-ScheduledTaskAction -Execute $exe -Argument $args
$trigger   = New-ScheduledTaskTrigger -Daily -At $Time
$settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopIfGoingOnBatteries `
                                          -AllowStartIfOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 10)

Register-ScheduledTask -TaskName $TaskName `
                       -Action $action -Trigger $trigger -Settings $settings `
                       -Description 'Exports Claude Code session state to sessions.json so the morning brief can read it.' `
                       -Force | Out-Null

Write-Host "Registered '$TaskName' — daily at $Time" -ForegroundColor Green
Write-Host "Output: $out" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Run it once now to seed the file:" -ForegroundColor Cyan
Write-Host "  Start-ScheduledTask -TaskName '$TaskName'" -ForegroundColor Green
Write-Host "Remove with: .\schedule-remove.ps1" -ForegroundColor DarkGray
