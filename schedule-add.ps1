<#
.SYNOPSIS
    Registers the Scheduled Tasks that connect the morning brief to this machine.

.DESCRIPTION
    The morning brief runs in a Linux sandbox that can only see mounted folders.
    ~/.claude/projects is a protected location and cannot be mounted, so the brief
    cannot enumerate Claude Code sessions on its own.

    Outbound (kk-sessions-dump, daily) runs SessionCli.exe --json on the host and drops the result
    next to this script, where the brief CAN read it. It writes two files,
    because the brief asks two different questions:

      sessions.json             a recency window, for "what happened yesterday".
      sessions-unfinished.json  every session still on the hook, however old.

    The second file exists because a recency cap silently loses work. A single
    --top 60 dump reached back only 1.2 days at current volume, so a session
    abandoned on Tuesday was invisible by Tuesday night and the brief could not
    tell "nothing happened" from "I cannot see it". --unfinished is unbounded in
    time and still small (17 sessions / 28 KB over 52 days), so nothing you have
    left open can age out of view.

    Inbound (kk-sessions-inbox, every few minutes): runs SessionCli.exe inbox on
    commands.json in the same folder, so what you decided in the brief — resume that
    one, tick this one off, drop that one — actually happens here. Nothing is queued
    most of the time and the verb says so and exits, which is why it can afford to
    run on a short interval.

    The inbox task runs interactively on purpose: resuming a session opens a terminal,
    and a task running in session 0 would open it where nobody can see it.

    Same pattern as ontario-address-changes\schedule-add.ps1.

.PARAMETER Time
    Daily run time for the dump, HH:mm. Default 06:45 — ahead of the 7am brief.

.PARAMETER InboxEvery
    Minutes between inbox runs. Default 5.

.PARAMETER SkipInbox
    Register only the daily dump, as this script did before the inbox existed.

.EXAMPLE
    .\schedule-add.ps1
    .\schedule-add.ps1 -Time 06:15 -InboxEvery 2
#>

[CmdletBinding()]
param(
    [string] $Time          = '06:45',
    [int]    $InboxEvery    = 5,
    [string] $TaskName      = 'kk-sessions-dump',
    [string] $InboxTaskName = 'kk-sessions-inbox',
    [switch] $SkipInbox
)

$ErrorActionPreference = 'Stop'

$here  = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe   = Join-Path $here 'dist\SessionCli.exe'
$out   = Join-Path $here 'sessions.json'
$open  = Join-Path $here 'sessions-unfinished.json'
$queue = Join-Path $here 'commands.json'

if (-not (Test-Path $exe)) { throw "Cannot find $exe - run .\publish.ps1 first" }

$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopIfGoingOnBatteries `
                                         -AllowStartIfOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 10)

# --- outbound: the scan the brief reads -------------------------------------

# SessionCli scans all sessions by default, so a project with several stalled
# sessions shows each one, not just the newest.
#
# The recency cap is sized for the narrative sections only. Keep it generous
# enough to cover a busy day end to end -- 90+ sessions can be touched in 24
# hours -- but not so large that the brief spends its context on settled work.
$argsRecent     = '--json "{0}" --top 150' -f $out
$argsUnfinished = '--json "{0}" --unfinished' -f $open

$action    = @(
    New-ScheduledTaskAction -Execute $exe -Argument $argsRecent
    New-ScheduledTaskAction -Execute $exe -Argument $argsUnfinished
)
$trigger   = New-ScheduledTaskTrigger -Daily -At $Time
$settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopIfGoingOnBatteries `
                                          -AllowStartIfOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 10)

Register-ScheduledTask -TaskName $TaskName `
                       -Action $action -Trigger $trigger -Settings $settings `
                       -Description 'Exports Claude Code session state to sessions.json and sessions-unfinished.json so the morning brief can read it.' `
                       -Force | Out-Null

Write-Host "Registered '$TaskName' — daily at $Time" -ForegroundColor Green
Write-Host "Output: $out" -ForegroundColor DarkGray
Write-Host "        $open" -ForegroundColor DarkGray

# --- inbound: what the brief decided -----------------------------------------

if (-not $SkipInbox) {
    # A queue that arrives while the machine is off is refused by the verb's own age
    # gate rather than replayed on the next boot, so a missed window costs a re-decision
    # and never a terminal opened against week-old reasoning.
    $inboxArgs = 'inbox --run "{0}"' -f $queue

    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date `
                                        -RepetitionInterval (New-TimeSpan -Minutes $InboxEvery)

    # Interactive, as the current user: the whole point is terminals you can see.
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
                                            -LogonType Interactive -RunLevel Limited

    Register-ScheduledTask -TaskName $InboxTaskName `
                           -Action (New-ScheduledTaskAction -Execute $exe -Argument $inboxArgs) `
                           -Trigger $trigger -Settings $settings -Principal $principal `
                           -Description 'Runs the commands the morning brief queued in commands.json.' `
                           -Force | Out-Null

    Write-Host "Registered '$InboxTaskName' — every $InboxEvery minute(s)" -ForegroundColor Green
    Write-Host "Watching: $queue" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Run the dump once now to seed the file:" -ForegroundColor Cyan
Write-Host "  Start-ScheduledTask -TaskName '$TaskName'" -ForegroundColor Green
Write-Host "Remove both with: .\schedule-remove.ps1" -ForegroundColor DarkGray
