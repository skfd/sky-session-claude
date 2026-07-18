<#
.SYNOPSIS
    Lists your Claude Code console sessions as ready-to-run resume one-liners.

.DESCRIPTION
    Reads ~/.claude/projects, and for each project folder finds the session
    transcript(s) and extracts the real working directory from the transcript
    itself (rather than guessing from the mangled folder name).

    Output is PowerShell-safe: statements are separated with ';' not '&&'.

.NOTES
    Prior art (tools that also list/resume sessions across projects). This tool
    goes further than all of them: it classifies each session's end-state
    (waiting-you / cut-off / limit / ...), estimates context %, surfaces the
    agent recap + last prompt, and renders a WinForms triage grid that tints
    unfinished work and emits copy-ready resume one-liners.

      - Claude Code built-in: `claude --resume` / `-r` opens a session picker;
        recent versions reportedly add an 'A' toggle for all-projects.
      - cc-sessions   - https://github.com/chronologos/cc-sessions (CLI list/resume)
      - clauhist      - reads ~/.claude/history.jsonl, browses via fzf
      - Claude Sessions Explorer - VS Code extension (ShahadIshraq.vscode-claude-sessions)
      - Claude History Viewer    - VS Code sidebar (diffs, search, resume)

.PARAMETER Top
    How many entries to show. Default 50.

.PARAMETER All
    Show every session in every project, not just the newest one per project.
    On by default; pass -All:$false to collapse to the newest session per project.

    In the interactive grid: press A to hide/show completed sessions, R to refresh.

.PARAMETER Copy
    Copy the resulting list to the clipboard.

.PARAMETER Cli
    Print the plain-text table + resume commands instead of the interactive grid.
    (The interactive Out-GridView window is the default.)

.PARAMETER Json
    Write the session list to this path as JSON and exit without any UI.
    Used by the morning brief, which runs in a sandbox that cannot read
    ~/.claude/projects itself (it is a protected location). Pair with
    schedule-add.ps1 to refresh the file on a timer.

.EXAMPLE
    .\Get-ClaudeSessions.ps1
    .\Get-ClaudeSessions.ps1 -Top 5
    .\Get-ClaudeSessions.ps1 -All -Top 50
    .\Get-ClaudeSessions.ps1 -Cli
    .\Get-ClaudeSessions.ps1 -Json "$env:USERPROFILE\Code\cowork\sessions.json"
#>

[CmdletBinding()]
param(
    [int]    $Top  = 50,
    [switch] $All  = $true,          # default: every session in every project (pass -All:$false for newest-per-project)
    [switch] $Copy,
    [switch] $Cli,
    [string] $Json,
    [int]    $ContextWindow = 200000   # token budget used to compute Ctx%; override for 1M-context sessions
)

$projects = Join-Path $env:USERPROFILE '.claude\projects'

if (-not (Test-Path $projects)) {
    Write-Warning "No Claude Code projects folder found at: $projects"
    return
}

# Collapse whitespace/newlines into a single clean line (no clipping).
function Format-Line {
    param([string] $Text)
    if (-not $Text) { return '' }
    return ($Text -replace '\s+', ' ').Trim()
}

# Same, but clipped to a single readable line for terminal output.
function Format-Snippet {
    param([string] $Text, [int] $Max = 160)

    $s = Format-Line $Text
    if ($s.Length -gt $Max) { $s = $s.Substring(0, $Max - 1) + [char]0x2026 }  # ellipsis
    return $s
}

# Human-friendly "how long ago", coarsened to the largest useful unit.
function Get-RelativeAge {
    param([datetime] $When)

    $span = (Get-Date) - $When
    if ($span.TotalMinutes -lt 1)  { return 'just now' }
    if ($span.TotalMinutes -lt 60) { return ('{0}m ago' -f [int]$span.TotalMinutes) }
    if ($span.TotalHours   -lt 24) { return ('{0}h ago' -f [int]$span.TotalHours) }
    $d = [int]$span.TotalDays
    return ('{0} day{1} ago' -f $d, $(if ($d -eq 1) { '' } else { 's' }))
}

# Read a transcript once and extract everything we display:
#   Cwd        - real working directory (any record carries "cwd")
#   Name       - the AI-generated session title, if one exists
#   LastPrompt - the operator's most recent prompt
#   Recap      - Claude Code's own session recap (system 'away_summary' record);
#                falls back to the agent's last text reply if recaps are off.
function Get-SessionInfo {
    param([string] $Path, [int] $ContextWindow = 200000)

    try {
        $lines = Get-Content -LiteralPath $Path -ErrorAction Stop
    } catch {
        return $null
    }

    $cwd = $null; $name = $null; $custom = $null; $prompt = $null
    $summary = $null; $lastText = $null; $userText = $null

    # Signals for the end-state classifier, tracked from the last real turn.
    $lastRole = $null; $lastStop = $null; $lastSynthetic = $false; $errText = $null
    $lastHasTool = $false; $lastEndsQ = $false
    $lastToolResult = $false; $lastInterrupt = $false; $ctxTokens = 0

    foreach ($line in $lines) {
        if ($line -notmatch '"(cwd|aiTitle|lastPrompt|type)"') { continue }
        try { $o = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }

        if (-not $cwd -and $o.cwd) { $cwd = $o.cwd }

        switch ($o.type) {
            'ai-title'     { if ($o.aiTitle)     { $name   = $o.aiTitle } }
            'custom-title' { if ($o.customTitle) { $custom = $o.customTitle } }
            'last-prompt'  { if ($o.lastPrompt)  { $prompt = $o.lastPrompt } }
            'system'      { if ($o.subtype -eq 'away_summary' -and $o.content) { $summary = $o.content } }
            'user' {
                $c = $o.message.content
                $utext = $null
                if ($c -is [string]) { $utext = $c }
                elseif ($c) {
                    $utext = ($c | Where-Object { $_.type -eq 'text' -and $_.text } | Select-Object -Last 1).text
                }
                if ($utext) { $userText = $utext }

                $lastRole       = 'user'
                $lastToolResult = [bool]($c -isnot [string] -and ($c | Where-Object { $_.type -eq 'tool_result' }))
                $lastInterrupt  = [bool]($utext -match '\[Request interrupted by user')
            }
            'assistant' {
                $msg = $o.message
                $t = ($msg.content | Where-Object { $_.type -eq 'text' -and $_.text } | Select-Object -Last 1).text
                $synthetic = ($msg.model -eq '<synthetic>') -or ($o.isApiErrorMessage -eq $true)

                $lastRole      = 'assistant'
                $lastStop      = $msg.stop_reason
                $lastSynthetic = $synthetic
                $lastHasTool   = [bool]($msg.content | Where-Object { $_.type -eq 'tool_use' })

                if ($synthetic) {
                    $errText = $t                       # keep the error text for classifying, not for the recap
                } else {
                    if ($t) { $lastText = $t; $lastEndsQ = [bool]($t.TrimEnd() -match '\?$') }
                    $u = $msg.usage
                    if ($u) {
                        $sum = [int]$u.input_tokens + [int]$u.cache_creation_input_tokens + [int]$u.cache_read_input_tokens
                        if ($sum -gt 0) { $ctxTokens = $sum }   # last real turn wins (context resets on compaction)
                    }
                }
            }
        }
    }

    if (-not $prompt)  { $prompt = $userText }   # fall back for older transcripts
    $recap = if ($summary) { $summary } else { $lastText }

    # ---- end-state classifier (see the 7-state list) ------------------------
    $status = 'complete'
    if ($lastSynthetic) {
        $low = "$errText".ToLower()
        if ($low -match 'spend limit|session limit|weekly|usage limit') { $status = 'limit' }
        else { $status = 'error' }
    }
    elseif ($lastRole -eq 'assistant') {
        if     ($lastHasTool -and $lastStop -eq 'tool_use') { $status = 'cut-off' }
        elseif ($lastStop -eq 'max_tokens')                 { $status = 'cut-off' }
        elseif ($lastEndsQ)                                 { $status = 'waiting-you' }
        else                                                { $status = 'complete' }
    }
    elseif ($lastRole -eq 'user') {
        if     ($lastInterrupt)   { $status = 'interrupted' }
        elseif ($lastToolResult)  { $status = 'cut-off' }        # died between tool result and next agent turn
        else                      { $status = 'waiting-agent' }
    }

    $ctxPct = if ($ctxTokens -gt 0) { [math]::Round(100 * $ctxTokens / $ContextWindow) } else { $null }

    [pscustomobject]@{
        Cwd           = $cwd
        Name          = if ($custom) { $custom } else { $name }   # your manual title wins over the AI one
        LastPrompt    = Format-Line $prompt
        Recap         = Format-Line $recap
        Status        = $status
        Complete      = ($status -eq 'complete')
        ContextTokens = $ctxTokens
        ContextPct    = $ctxPct
    }
}

# Interactive WinForms grid with wrapping Prompt/Recap cells. Returns the resume
# commands of the rows the user selected (empty if they cancel).
function Show-SessionGrid {
    param([object[]] $Rows)

    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()

    # Mutable state the key handlers write to (a hashtable is shared by reference
    # with the event closures, unlike a plain variable).
    $state = @{ Refresh = $false; HideCompleted = $false }

    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'Claude Code sessions - double-click to resume | A: hide/show completed | R: refresh | select rows + Copy'
    $form.StartPosition = 'CenterScreen'
    $form.Size = New-Object System.Drawing.Size(1150, 680)
    $form.KeyPreview = $true

    $grid = New-Object System.Windows.Forms.DataGridView
    $grid.Dock = 'Fill'
    $grid.ReadOnly = $true
    $grid.AllowUserToAddRows = $false
    $grid.AllowUserToResizeRows = $false
    $grid.RowHeadersVisible = $false
    $grid.SelectionMode = 'FullRowSelect'
    $grid.MultiSelect = $true
    $grid.AutoGenerateColumns = $false
    $grid.AutoSizeRowsMode = 'AllCells'          # rows grow to fit wrapped text
    $grid.ColumnHeadersHeightSizeMode = 'AutoSize'
    $grid.BackgroundColor = [System.Drawing.SystemColors]::Window
    $grid.DefaultCellStyle.SelectionBackColor = [System.Drawing.Color]::FromArgb(197, 220, 245)
    $grid.DefaultCellStyle.SelectionForeColor = [System.Drawing.Color]::Black

    function New-Col($name, $header, $fill, $wrap) {
        $c = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
        $c.Name = $name; $c.HeaderText = $header
        if ($fill -gt 0) { $c.AutoSizeMode = 'Fill'; $c.FillWeight = $fill }
        else { $c.AutoSizeMode = 'AllCells' }
        $c.DefaultCellStyle.WrapMode = if ($wrap) { 'True' } else { 'False' }
        $c.DefaultCellStyle.Alignment = 'TopLeft'
        $c
    }

    [void]$grid.Columns.Add((New-Col 'LastActive' 'Last active' 0  $true))
    [void]$grid.Columns.Add((New-Col 'Name'       'Name'        0  $false))
    [void]$grid.Columns.Add((New-Col 'Project'    'Project'     0  $false))
    [void]$grid.Columns.Add((New-Col 'Status'     'Status'      0  $false))
    [void]$grid.Columns.Add((New-Col 'Ctx'        'Ctx%'        0  $false))
    [void]$grid.Columns.Add((New-Col 'LastPrompt' 'Last prompt' 34 $true))
    [void]$grid.Columns.Add((New-Col 'Recap'      'Agent recap' 46 $true))
    [void]$grid.Columns.Add((New-Col 'SizeKB'     'KB'          0  $false))
    $cmd = New-Col 'Command' 'Command' 0 $false; $cmd.Visible = $false
    [void]$grid.Columns.Add($cmd)

    foreach ($r in $Rows) {
        $when = "{0}`n{1}" -f $r.LastActive.ToString('yyyy-MM-dd HH:mm'), (Get-RelativeAge $r.LastActive)
        $ctx  = if ($null -ne $r.ContextPct) { "$($r.ContextPct)%" } else { '' }
        [void]$grid.Rows.Add(
            $when,
            $r.Name, $r.Project, $r.Status, $ctx, $r.LastPrompt, $r.Recap, $r.SizeKB, $r.Command)
    }

    # Tint incomplete rows so the eye lands on sessions that still need attention.
    foreach ($gr in $grid.Rows) {
        if ($gr.Cells['Status'].Value -ne 'complete') {
            $gr.DefaultCellStyle.BackColor = [System.Drawing.Color]::FromArgb(255, 248, 225)  # pale amber
        }
    }

    # Double-click a row -> open a new terminal in that folder and resume the session.
    $grid.Add_CellDoubleClick({
        param($sender, $e)
        if ($e.RowIndex -lt 0) { return }
        $command = $sender.Rows[$e.RowIndex].Cells['Command'].Value
        if ($command) {
            Start-Process powershell.exe -ArgumentList '-NoExit', '-Command', $command
        }
    })

    # A -> toggle visibility of completed rows.  R -> rebuild the list.
    $form.Add_KeyDown({
        param($sender, $e)
        switch ($e.KeyCode) {
            'A' {
                $state.HideCompleted = -not $state.HideCompleted
                $grid.CurrentCell = $null   # can't hide the current row otherwise
                foreach ($gr in $grid.Rows) {
                    if ($gr.Cells['Status'].Value -eq 'complete') {
                        $gr.Visible = -not $state.HideCompleted
                    }
                }
                $e.Handled = $true
            }
            'R' {
                $state.Refresh = $true
                $form.Close()
                $e.Handled = $true
            }
        }
    })

    $panel = New-Object System.Windows.Forms.FlowLayoutPanel
    $panel.Dock = 'Bottom'; $panel.FlowDirection = 'RightToLeft'
    $panel.Height = 46; $panel.Padding = New-Object System.Windows.Forms.Padding(8)

    $btnOk = New-Object System.Windows.Forms.Button
    $btnOk.Text = 'Copy resume command(s)'; $btnOk.AutoSize = $true
    $btnOk.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = 'Cancel'; $btnCancel.AutoSize = $true
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $panel.Controls.AddRange(@($btnOk, $btnCancel))

    $form.Controls.Add($grid)
    $form.Controls.Add($panel)
    $form.AcceptButton = $btnOk
    $form.CancelButton = $btnCancel

    $result = $form.ShowDialog()

    if ($state.Refresh) {
        return [pscustomobject]@{ Refresh = $true;  Commands = @() }
    }
    $commands = if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
        @($grid.SelectedRows | Sort-Object Index | ForEach-Object { $_.Cells['Command'].Value })
    } else { @() }
    return [pscustomobject]@{ Refresh = $false; Commands = $commands }
}

# Scan ~/.claude/projects and build the display rows. Factored into a function
# so the grid's Refresh (R) hotkey can rebuild the list without restarting.
function Get-SessionRows {
    param([string] $Projects, [switch] $All, [int] $Top, [int] $ContextWindow)

    $files = Get-ChildItem -LiteralPath $Projects -Recurse -Filter *.jsonl -File -ErrorAction SilentlyContinue
    if (-not $files) { return @() }

    # Newest session per project folder, unless -All was requested.
    if (-not $All) {
        $files = $files |
            Group-Object DirectoryName |
            ForEach-Object { $_.Group | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
    }

    $files |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First $Top |
        ForEach-Object {
            $info = Get-SessionInfo -Path $_.FullName -ContextWindow $ContextWindow
            $cwd  = $info.Cwd
            if (-not $cwd) { $cwd = '<unknown - cwd not found in transcript>' }

            # "Unfinished" = the transcript ends on the agent asking something, or on
            # an operator prompt the agent never answered in text. Both mean the
            # thread stopped waiting on a human rather than on a delivered result.
            $recap  = $info.Recap
            $openQ  = [bool]($recap -and $recap.TrimEnd() -match '\?$')
            $noReply = [bool]($info.LastPrompt -and -not $recap)

            [pscustomobject]@{
                LastActive = $_.LastWriteTime
                AgeDays    = [math]::Round(((Get-Date) - $_.LastWriteTime).TotalDays, 1)
                Name       = if ($info.Name) { $info.Name } else { '(untitled)' }
                Project    = Split-Path $cwd -Leaf
                Status     = $info.Status
                Complete   = $info.Complete
                ContextPct = $info.ContextPct
                ContextTokens = $info.ContextTokens
                LastPrompt = $info.LastPrompt
                Recap      = $recap
                Unfinished = ($openQ -or $noReply)
                WaitingOn  = if ($noReply) { 'agent' } elseif ($openQ) { 'you' } else { '' }
                Cwd        = $cwd
                SessionId  = $_.BaseName
                SizeKB     = [math]::Round($_.Length / 1KB, 1)
                Command    = 'cd "{0}"; claude --resume {1}' -f $cwd, $_.BaseName
            }
        }
}

$rows = @(Get-SessionRows -Projects $projects -All:$All -Top $Top -ContextWindow $ContextWindow)

if (-not $rows) {
    Write-Warning "No session transcripts (*.jsonl) found under: $projects"
    return
}

# ---- JSON export (headless; for the morning brief) --------------------------
if ($Json) {
    $dir = Split-Path -Parent $Json
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    [pscustomobject]@{
        GeneratedAt = (Get-Date).ToString('o')
        Count       = @($rows).Count
        Sessions    = @($rows | Select-Object LastActive, AgeDays, Name, Project,
                                              Status, Complete, ContextPct, ContextTokens,
                                              LastPrompt, Recap, Unfinished, WaitingOn,
                                              Cwd, SessionId, SizeKB, Command)
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Json -Encoding UTF8

    Write-Host ("Wrote {0} session(s) to {1}" -f @($rows).Count, $Json) -ForegroundColor Yellow
    return
}

# ---- Interactive UI: WinForms grid (default; wrapping prompt/recap cells) ---
if (-not $Cli) {
    # Loop so the R hotkey can rebuild the list and re-show the grid.
    do {
        $result = Show-SessionGrid -Rows $rows
        if ($result.Refresh) {
            $rows = @(Get-SessionRows -Projects $projects -All:$All -Top $Top -ContextWindow $ContextWindow)
            if (-not $rows) { Write-Warning "Nothing to show."; return }
        }
    } while ($result.Refresh)
    $picked = $result.Commands

    if ($picked) {
        ($picked -join [Environment]::NewLine) | Set-Clipboard
        Write-Host ("Copied {0} resume command(s) to the clipboard:" -f @($picked).Count) -ForegroundColor Yellow
        foreach ($c in $picked) { Write-Host $c -ForegroundColor Green }
    } else {
        Write-Host "No rows selected." -ForegroundColor DarkGray
    }
    return
}

Write-Host ""
Write-Host "Claude Code sessions - most recent first" -ForegroundColor Cyan
Write-Host ("-" * 60) -ForegroundColor DarkGray

$rows | Format-Table `
    @{ L = 'Last active'; E = { $_.LastActive.ToString('yyyy-MM-dd HH:mm') } },
    @{ L = 'Name';        E = { $_.Name } },
    @{ L = 'Project';     E = { $_.Project } },
    @{ L = 'Status';      E = { $_.Status } },
    @{ L = 'Ctx%';        E = { if ($null -ne $_.ContextPct) { "$($_.ContextPct)%" } else { '' } }; A = 'right' },
    @{ L = 'KB';          E = { $_.SizeKB }; A = 'right' } `
    -AutoSize

Write-Host "Resume commands:" -ForegroundColor Cyan
Write-Host ("-" * 60) -ForegroundColor DarkGray

foreach ($r in $rows) {
    Write-Host $r.Command -ForegroundColor Green
    Write-Host ("    # {0}  {1}" -f $r.LastActive.ToString('yyyy-MM-dd HH:mm'), $r.Name) -ForegroundColor DarkGray
    if ($r.LastPrompt) { Write-Host ("    prompt: {0}" -f $r.LastPrompt) -ForegroundColor Gray }
    if ($r.Recap)      { Write-Host ("    recap : {0}" -f $r.Recap)      -ForegroundColor DarkGray }
}

Write-Host ""
Write-Host ("Tip: 'cd `"<project>`"; claude -c' continues that folder's most recent session.") -ForegroundColor DarkGray
Write-Host ""

if ($Copy) {
    ($rows | ForEach-Object { $_.Command }) -join [Environment]::NewLine | Set-Clipboard
    Write-Host "Copied $($rows.Count) command(s) to the clipboard." -ForegroundColor Yellow
}

# Leave the objects on the pipeline so you can post-process, e.g.
#   .\Get-ClaudeSessions.ps1 | Where-Object Project -like '*toronto*'
$rows
