# Measures the declaration convention: of the sessions active since the convention landed,
# what fraction actually carry a live declaration? (docs/PROJECT-STATE-PLAN.md, step 4 —
# this number decides whether the Stop hook gets built.)
#
# Buckets, and why there are four:
#   declared  — a claim that still stands. The convention worked.
#   stale     — a claim the operator has prompted past. The agent reported once and things
#               moved on; a re-declare was owed and did not come.
#   mid-turn  — taking a turn right now, so it has not reached its declare point and cannot
#               be judged. Best effort: a harness under the SDK publishes no busy/idle, so
#               some of these will land in "silent" instead — including whichever session
#               is running this script.
#   silent    — ended a turn since the window opened and said nothing. This is the bucket
#               the Stop hook would drain.
#
# The headline fraction is declared / (declared + stale + silent) — mid-turn rows are
# excluded from the denominator because they still might declare.
param(
    # When the convention landed in ~/.claude/CLAUDE.md. Not a week yet; the reading that
    # decides the Stop hook is the one taken on or after 2026-09-05.
    [datetime]$Since = '2026-08-29T01:14:00',
    [string]$Cli = "$PSScriptRoot/dist/SessionCli.exe"
)

$ErrorActionPreference = 'Stop'

$dump = & $Cli list | ConvertFrom-Json
if ($dump.Warning) { Write-Warning $dump.Warning }

$window = @($dump.Sessions | Where-Object { [datetime]$_.LastActive -ge $Since })

$declared = @($window | Where-Object { $_.Declared -ne 'none' })
$stale    = @($window | Where-Object { $_.Declared -eq 'none' -and $_.DeclaredStale })
$midturn  = @($window | Where-Object {
    $_.Declared -eq 'none' -and -not $_.DeclaredStale -and $_.Live -and $_.Live.State -eq 'busy' })
$silent   = @($window | Where-Object {
    $_.Declared -eq 'none' -and -not $_.DeclaredStale -and -not ($_.Live -and $_.Live.State -eq 'busy') })

$judged = $declared.Count + $stale.Count + $silent.Count
$fraction = if ($judged -gt 0) { [math]::Round(100.0 * $declared.Count / $judged, 1) } else { 0 }

"Window: sessions active since $($Since.ToString('yyyy-MM-dd HH:mm')) — $($window.Count) session(s), $judged judged"
"  declared : $($declared.Count)"
"  stale    : $($stale.Count)"
"  mid-turn : $($midturn.Count)  (excluded from the fraction)"
"  silent   : $($silent.Count)"
""
"Live declaration fraction: $fraction%"

if ($silent.Count -gt 0) {
    ""
    "Silent sessions (the rows a Stop hook would speak for):"
    $silent | Sort-Object Project, LastActive |
        Format-Table @{L = 'Project'; E = { $_.Project } },
                     @{L = 'Session'; E = { $_.SessionId.Substring(0, 8) } },
                     @{L = 'Status'; E = { $_.Status } },
                     @{L = 'LastActive'; E = { ([datetime]$_.LastActive).ToString('MM-dd HH:mm') } },
                     @{L = 'Name'; E = { $_.Name } } -AutoSize
}
