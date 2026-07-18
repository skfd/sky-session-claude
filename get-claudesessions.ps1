<#
.SYNOPSIS
    Lists your Claude Code console sessions as ready-to-run resume one-liners.

.DESCRIPTION
    Reads ~/.claude/projects, and for each project folder finds the session
    transcript(s) and extracts the real working directory from the transcript
    itself (rather than guessing from the mangled folder name).

    Output is PowerShell-safe: statements are separated with ';' not '&&'.

.PARAMETER Top
    How many entries to show. Default 20.

.PARAMETER All
    Show every session in every project, not just the newest one per project.

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
    [int]    $Top  = 20,
    [switch] $All,
    [switch] $Copy,
    [switch] $Cli,
    [string] $Json
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

# Read a transcript once and extract everything we display:
#   Cwd        - real working directory (any record carries "cwd")
#   Name       - the AI-generated session title, if one exists
#   LastPrompt - the operator's most recent prompt
#   Recap      - Claude Code's own session recap (system 'away_summary' record);
#                falls back to the agent's last text reply if recaps are off.
function Get-SessionInfo {
    param([string] $Path)

    try {
        $lines = Get-Content -LiteralPath $Path -ErrorAction Stop
    } catch {
        return $null
    }

    $cwd = $null; $name = $null; $prompt = $null
    $summary = $null; $lastText = $null; $userText = $null

    foreach ($line in $lines) {
        if ($line -notmatch '"(cwd|aiTitle|lastPrompt|type)"') { continue }
        try { $o = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }

        if (-not $cwd -and $o.cwd) { $cwd = $o.cwd }

        switch ($o.type) {
            'ai-title'    { if ($o.aiTitle)    { $name   = $o.aiTitle } }
            'last-prompt' { if ($o.lastPrompt) { $prompt = $o.lastPrompt } }
            'system'      { if ($o.subtype -eq 'away_summary' -and $o.content) { $summary = $o.content } }
            'user' {
                $c = $o.message.content
                if ($c -is [string]) { if ($c) { $userText = $c } }
                elseif ($c) {
                    $t = ($c | Where-Object { $_.type -eq 'text' -and $_.text } | Select-Object -Last 1).text
                    if ($t) { $userText = $t }
                }
            }
            'assistant' {
                $t = ($o.message.content | Where-Object { $_.type -eq 'text' -and $_.text } | Select-Object -Last 1).text
                if ($t) { $lastText = $t }
            }
        }
    }

    if (-not $prompt)  { $prompt = $userText }   # fall back for older transcripts
    $recap = if ($summary) { $summary } else { $lastText }

    [pscustomobject]@{
        Cwd        = $cwd
        Name       = $name
        LastPrompt = Format-Line $prompt
        Recap      = Format-Line $recap
    }
}

# Interactive WinForms grid with wrapping Prompt/Recap cells. Returns the resume
# commands of the rows the user selected (empty if they cancel).
function Show-SessionGrid {
    param([object[]] $Rows)

    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()

    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'Claude Code sessions - select rows, then Copy resume command(s)'
    $form.StartPosition = 'CenterScreen'
    $form.Size = New-Object System.Drawing.Size(1150, 680)

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

    [void]$grid.Columns.Add((New-Col 'LastActive' 'Last active' 0  $false))
    [void]$grid.Columns.Add((New-Col 'Name'       'Name'        0  $false))
    [void]$grid.Columns.Add((New-Col 'Project'    'Project'     0  $false))
    [void]$grid.Columns.Add((New-Col 'LastPrompt' 'Last prompt' 34 $true))
    [void]$grid.Columns.Add((New-Col 'Recap'      'Agent recap' 46 $true))
    [void]$grid.Columns.Add((New-Col 'SizeKB'     'KB'          0  $false))
    $cmd = New-Col 'Command' 'Command' 0 $false; $cmd.Visible = $false
    [void]$grid.Columns.Add($cmd)

    foreach ($r in $Rows) {
        [void]$grid.Rows.Add(
            $r.LastActive.ToString('yyyy-MM-dd HH:mm'),
            $r.Name, $r.Project, $r.LastPrompt, $r.Recap, $r.SizeKB, $r.Command)
    }

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
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) { return @() }
    return @($grid.SelectedRows | Sort-Object Index | ForEach-Object { $_.Cells['Command'].Value })
}

$files = Get-ChildItem -LiteralPath $projects -Recurse -Filter *.jsonl -File -ErrorAction SilentlyContinue

if (-not $files) {
    Write-Warning "No session transcripts (*.jsonl) found under: $projects"
    return
}

# Newest session per project folder, unless -All was requested.
if (-not $All) {
    $files = $files |
        Group-Object DirectoryName |
        ForEach-Object { $_.Group | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
}

$rows = $files |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First $Top |
    ForEach-Object {
        $info = Get-SessionInfo -Path $_.FullName
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

if (-not $rows) {
    Write-Warning "Nothing to show."
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
                                              LastPrompt, Recap, Unfinished, WaitingOn,
                                              Cwd, SessionId, SizeKB, Command)
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Json -Encoding UTF8

    Write-Host ("Wrote {0} session(s) to {1}" -f @($rows).Count, $Json) -ForegroundColor Yellow
    return
}

# ---- Interactive UI: WinForms grid (default; wrapping prompt/recap cells) ---
if (-not $Cli) {
    $picked = Show-SessionGrid -Rows $rows

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
