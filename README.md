# <img src="docs/icon.png" width="30" align="top" alt=""> Sky Session Claude

A tiny Windows desktop app that shows all your **Claude Code sessions** in one place and lets you jump back into any of them with a double-click.

Claude Code stores every session as a session file (a `.jsonl`) under `~/.claude/projects`. Once you have dozens of them across several repos, finding the one you want — *"which session was I in when I asked it to fix the migration?"* — turns into archaeology. This app scans those session files and lays them out as a filterable list of cards so you can see at a glance what each session was doing, whether it finished, and how full its context got.

![Sky Session Claude — the session list](docs/screenshot.png)

It follows the Windows apps theme, so the same list in dark mode:

![Sky Session Claude — dark mode](docs/screenshot-dark.png)

## What it shows

Each card is one session, four lines tall:

| Line | Meaning |
|---|---|
| **Title** | Session name, plus the repo it belongs to and how long ago it was last worked on |
| **Prompt** *(italic)* | Your most recent message in that session |
| **Recap** | A short summary of what the agent last did, clipped to two lines (hover for the rest) |
| **Meta** | `complete`, `waiting-you`, `waiting-agent`, `cut-off`, `limit`, `error`, `interrupted` · how full the context window is (auto-detects 1M-token sessions) · session file size on disk |

Cards are a fixed height, so one long recap can never push the rest of the list off screen. Unfinished sessions get a coloured stripe down their left edge so your eye lands on the ones still waiting on you; completed ones have none. ("Unfinished" = every Status except `complete`.)

The age is the **last real turn** in the session file, not the file's timestamp on disk. Resuming a session appends bookkeeping records (mode, titles, last prompt) the moment it opens, so a file's last-write time says "just now" even when you opened a session, looked at it, and typed nothing — which is exactly when you most want to know it has been sitting for three weeks. A fork still reads as new, though — the age is floored at the file's own creation time, so a fresh file full of copied records doesn't inherit the age of the conversation it branched from.

When a session *was* opened after its last turn and nothing came of it, the card shows both ends: **`2 days ago → 1h ago`** — last worked on two days ago, last opened an hour ago. Hover for the exact times. Sessions being actively worked on show one age, since there the file is rewritten seconds after every turn.

## How Status is decided

Status is read from the **last real turn** — the final meaningful record in the session file, after skipping attachment/snapshot noise. The vocabulary below is used throughout the code and docs; the full list lives in [`docs/GLOSSARY.md`](docs/GLOSSARY.md).

- **Operator** — you, the human who types prompts. **Agent** — Claude, doing the work. (These stay distinct from the raw JSON `user`/`assistant` roles, which are more overloaded than they look.)
- A `user`-role record is one of three **turns**: an **operator turn** (you typed text), a **tool-result turn** (a `tool_result` came back), or a **harness turn** (tooling injected it — `<system-reminder>`, `/clear`, `<task-notification>`).
- A **close-out** is a terminal operator turn that thanks rather than asks ("thank you", "all good"). It reads as done — though usually the agent has already replied, so the session is `complete` regardless.

So: last real turn is an agent turn → `complete` (or `waiting-you` if it ends in a question); an operator/harness turn → `waiting-agent`; a stalled tool step → `cut-off`; an error/limit record → `error`/`limit`.

## What it does

- **Double-click a card** → if that session is already open in a terminal, jumps to that window; otherwise opens a new PowerShell terminal in that repo and runs `claude --resume <id>`, dropping you straight back into the session. (Windows Terminal is focused window-level — it has no public API to select a specific tab.)
- **Copy resume command(s)** → copies the resume command for every selected card to the clipboard.
- **Fork a session** (**F**) → branch the selected session into a new one, picking where it branches off: **at the tip** (the official `claude --resume --fork-session`) or **from just before any earlier prompt**. The from-a-point fork writes a new session file containing only the conversation up to that moment (session records form a `uuid`/`parentUuid` tree, so the app copies the chosen record's ancestry under a fresh session id) and resumes it — handy for "back to before I asked it to do X, but keep the original too". The original session is never modified; a fork you don't like is just a session file you delete. Note the record format is internal to Claude Code, so a future CLI version could change it — worst case a fork fails to resume, the original is always safe.
- **Live updates** → a filesystem watcher refreshes cards automatically as sessions change (toggle off with the **Live** checkbox).
- **Filter** by search text, status, or project; hide completed sessions; scope to the current project or all projects; cap how many sessions load (defaults to **All**, so an old unfinished session can never hide just past a cut-off; drop to 50 → 500 if you want a shorter scan).
- **Abandon a session** (**X**) → crosses out sessions you're not going back to. They stay honestly classified as unfinished — abandoning is your judgment, not the classifier's, so it never changes the status. Abandoned cards are hidden until you tick **Show abandoned**, which shows them struck through. The marks persist in `%APPDATA%\sky-session-claude\abandoned.json`.
- **Dark mode** → follows the Windows apps theme, title bar included, and switches live when you flip the system setting — no restart, and no in-app toggle to keep in sync. The window and taskbar icon switch too: by night the cloud gets a moon and stars <img src="docs/icon-night.png" width="20" align="top" alt="">.

### Keyboard shortcuts

- **R** — refresh
- **A** — hide/show completed sessions
- **X** — abandon/restore the selected session(s)
- **F** — fork the selected session (at the tip, or from before any earlier prompt)

## Install

1. Download **`SkySessionClaude.exe`** from the [latest release](https://github.com/skfd/sky-session-claude/releases/latest).
2. Run it. That's it — it's a single self-contained file, no .NET runtime or installer required.

Windows SmartScreen may warn about an unrecognized app the first time (the binary is unsigned). Click **More info → Run anyway**.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
# Run in-place
dotnet run --project src/SessionApp

# Produce the release single-file exe in dist/ AND refresh the stable install
# under %LOCALAPPDATA%\Programs\SkySessionClaude (what the Start-menu shortcut runs)
./publish.ps1

# Just build dist/, leave the installed app alone
./publish.ps1 -SkipInstall
```

## Headless mode

Some tools (like the morning brief) run in a sandbox that can't read `~/.claude/projects` directly. For them, **`SessionCli.exe`** scans the same sessions and writes the list as JSON — no window, no UI. It shares `SessionCore` with the app, so both classify status the same way.

```powershell
SessionCli.exe                       # JSON to stdout
SessionCli.exe --json <path>         # JSON to a file (parent dirs created)
SessionCli.exe --top <n>             # cap sessions (default 50)
SessionCli.exe --newest-per-project  # one session per project (default: all)
SessionCli.exe --context-window <n>  # token budget for Ctx% (default 200000)
```

Ctx% switches to a 1M budget for sessions detected on the extended context window: either a turn exceeded 200k tokens, or the session ran on the model configured with the `[1m]` suffix in `~/.claude/settings.json` (transcripts don't record the window themselves).

A scheduled task on the host runs `SessionCli.exe --json <path>` to refresh a file the sandbox can then read — see `schedule-add.ps1`.

## Project layout

- **`src/SessionCore`** — session scanning, session-file parsing, status detection, live-refresh cache/watcher (no UI dependencies).
- **`src/SessionApp`** — the WPF card list and view model; `Theme/` holds the light/dark palettes, the themed control chrome, and the system-theme watcher.
- **`src/SessionCli`** — headless JSON scanner for the morning brief (shares `SessionCore`).
- **`src/SessionCore.Tests`** — unit tests for the core.
- **`schedule-add.ps1`** / **`schedule-remove.ps1`** — register/remove the daily task that refreshes `sessions.json` for the morning brief.

## License

MIT
