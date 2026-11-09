# Next

What is queued for this repo, one item per heading. A session picking up work
starts here; a session finishing one removes it or rewrites it as what is left.

## A NEXT.md queue in every repo, shown by Sky

"Handoff" came up in 29 prompts and "what's next" in 12 over Sep 8 – Nov 7.
Give each repo one file — this one is the first — that says what is queued,
and let Sky show it beside the session list, so "what's next" in a project is
answered without opening a session. Decide the format (plain headings, or
something with ready/blocked like Beads' `bd ready`) and what Sky shows:
count, first heading, or the lot.

## Smoke tests: ship the quieter keeper log

Shipped in v1.16.0 (2026-11-09): the test gate, `SharedStores`, `SessionCli
doctor` with Sky's heartbeat, and `publish.ps1` relaunching with `--standby`.
The task "Sky Session Claude doctor" runs at 06:45 and 17:30. The release
notes have their slice, taken with the new `--snapshot` flag.

Shipped in v1.17.0: the keeper writes a held reason only when it changes
(`KeeperNotes`), and `standby.log` trims at 1 MB instead of 256 KB. A backoff
still logs every minute, because its countdown changes, so the doctor's
five-minute rule holds.

Changelog slices from now on: `SkySessionClaude.exe --snapshot <png>
--element StatusBar` (or no `--element` for the whole window, `--view
projects` for that view, `--search <text>` to narrow the list to one row)
draws the window with no screen needed. The
whole-window shot shows real session titles, so crop before publishing.

## `/clear`: show an inherited name on the card

`clear-start` is wired as a `SessionStart` hook (matcher `clear`) since
2026-11-09. Optional later: show `TitleInherited` on the card (dimmed name).

## "Done" checked by a hook, not just declared

`stop-check` only checks that a state was declared. Discuss a per-repo check —
build or tests pass before a session may declare `exhausted`/`needs-read` —
the way the Claude Code docs and Mitchell Hashimoto turn must-hold rules into
hooks.

Decided with the user 2026-11-09: **at the declaration, record only.**
- Evidence was thin: one "build failed" prompt in all transcripts, and that
  was CI, not a session leaving the tree broken. So measure first, as the
  Stop hook itself was.
- `state exhausted|needs-read` runs the repo's check, if the repo defines
  one (opt-in by presence; most repos have none and are never checked).
  `runnable`/`blocked` are not checked: a broken build with work queued is
  honest.
- The result (pass/fail, commit, when) is stamped on the declaration and Sky
  shows "checked" vs merely declared. Nothing is refused. Not in
  `stop-check`: a build will not fit its 15 s timeout, and a timed-out hook
  silently never blocks.
- A check that cannot run (missing tool, timeout) records "not run", never
  "fail".
- Revisit after a few weeks of data: if "done but failing" shows up, turn on
  one refusal that names `runnable --note` as the way out.

Built 2026-11-09 on main's dev build: `.claude/sky-check.json`
(`{"command": "...", "timeoutSeconds": 600}`), found from the agent's shell
folder first under `--self` (a worktree), then the session's folder; the
result is on the declaration, `DeclaredCheck` on list rows,
`Check`/`CheckCommit` on `--projects`, a "✓ checked" / "✗ check failed"
mark on Sky's project rows, and counted by `measure-declarations.ps1`. This
repo's check is `dotnet test` (~12 s).

v1.17.0 is installed and published on GitHub (2026-11-09). `~/.claude/CLAUDE.md` now says a done-declaration may take
minutes and what to do with FAILED. Active repos with tests got their own
`sky-check.json` where the check passed today.

Left: read the numbers with `measure-declarations.ps1` on or after
2026-11-30 and decide whether a failing check should refuse once.

## Park projects on purpose: design + catalogue review

Of 160 repos under `~/Code`, 40 of the 80 outside any family had no commit in
30+ days, and 24 of 25 `experiment-*` repos are idle (runner paused since
09-20).

Decided with the user 2026-11-08:
- **Status is declared, not derived.** Last-commit age lies here: ~25 repos
  look 2 weeks fresh only from cross-repo sweeps (GoatCounter, JOSM links,
  title-casing), and 5 are fed by scheduled jobs. Age, sessions and
  sweep/bot detection only *propose* a status during review.
- **Vocabulary:** active / parked / done / automated (a scheduled job keeps
  it alive; off the list, but still flagged if the job breaks). Parked and
  done carry a note: why, and what would wake it.
- **Store:** Sky's own, a `ProjectStatusStore` beside `declarations.json` and
  `dispositions.json`. A CLI verb sets it, globs allowed
  (`park experiment-* --note "runner paused"`), non-git folders too.
  `--projects` carries a `Status` field.
- **Consumers:** Sky's list hides parked/done by default; morning brief,
  `rc`/recent-projects-on-phone, stale sweeps and the palace quest log skip
  them; parking clears a lingering declared state (battle-agents,
  obsidian-personal, historical-presence-map have sat `blocked` 47–59 days);
  the palace draws parked fog distinct from forgotten fog.
- **Families are tagged in the same review pass** (city `*-address-import`,
  `guelph-*`/`toronto-*` layers are all untagged in
  `mind-palace/families.json`); parking a family parks its members.

Built 2026-11-09 (`5a13350`): `SessionCli project <standing> <names>`,
`standings.json`, `Standing` on `--projects` rows; `--unfinished`, standby and
the app's default views and tray count skip set-aside projects. Reaches the
installed CLI in v1.16.0.

Reviewed with the user 2026-11-09: 106 of ~156 repos set aside (40 parked,
47 done, 19 automated; `SessionCli project` lists them with notes). The
`skfd` duplicate went to the Recycle Bin (its one unmerged branch is in
`skfd-profile`), `obsidian`'s notes were committed (it stays active), and
`families.json` gained `address` and `layers` (mind-palace `45cda09`).

Left: the palace reading `Standing` and drawing parked fog apart from forgotten fog;
automated projects whose job breaks still surfacing.

## Know whether the user is asleep (Apple Watch)

The schedule in `~/.claude/CLAUDE.md` says asleep from 00:00, which is the
aim; prompts often run to 02:00. Instead of guessing from the clock: an iOS
Shortcuts personal automation on Sleep Focus on/off (the watch's sleep
schedule drives it) posts `asleep`/`awake` to a small endpoint; SessionCli
reads it, and the scheduler, `stop-check` and push notifications respect it.

Designed with the user 2026-11-08, not started ("not yet"):

- Two facts, three modes. Awake: runs and pings. Asleep away: runs, pings
  held. Asleep at home: quiet, nothing starts and nothing pings.
- One shortcut on Sleep Focus on/off sends both facts: asleep/awake, and
  home = current Wi-Fi is the home network. No separate geofence needed.
- Sleep Focus is schedule + manual toggle + alarm dismissal, not measured
  sleep (HealthKit sleep samples land the next morning).
- `SessionCli presence` -> `{mode, since, source, stale}`; scheduled tasks,
  `stop-check`, push notifications and the Sky tray read it.
- A prompt into any session on this PC counts as awake until the next
  Sleep-on. A state older than ~20h is stale and the clock in
  `~/.claude/CLAUDE.md` takes over.
- Open: the transport. Worker + KV (recommended), an ntfy topic (nothing to
  deploy), or Tailscale (if adopted, the PC becomes reachable directly).

## What `new --prompt` left for later

`new --prompt` and `--window` shipped in v1.15.3-dev (2026-11-09). Two pieces
were left out on purpose:
- **Job objects.** Built 2026-11-09 on the dev build (`OutsideJob`). When
  the launcher has package identity, or descends from the desktop app's own
  `claude.exe` (under `WindowsApps\Claude_`), every terminal goes through
  `Win32_Process.Create` and is the WMI host's child. Everywhere else the
  launch is unchanged. Tests prove the WMI path (parent is WmiPrvSE, folder
  honoured) and the ancestry walk. A WMI-opened terminal is visible with
  `claude`/`wt` on PATH, and a single-file published `SessionCli` starts
  processes through WMI. `SessionCli doctor` has a `launch` line saying
  which way and why.

  Checked on the live machine 2026-11-09, with no desktop-app session
  running:
  - Package identity does not carry. A process `Invoke-CommandInDesktopPackage`
    starts has it, and its children (via cmd or PowerShell) do not. So the
    ancestry check is the one that fires.
  - A process started with the app's main `claude.exe` as its parent
    (`PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`) ran the dev `doctor`, which said
    "go through WMI, because it was started by the Claude desktop app". `new`
    from there opened its terminal.
  - The app's own job is not the killer. It holds 347 processes, among them
    Chrome, Firefox, Discord and terminal Claude sessions, and its main
    process's children inherit it. So what killed Sky in October hangs off
    the CLI process the app starts per session (its tree or its own job),
    which only a real desktop-app session has.
  - Quitting the app to watch was refused by the auto-mode classifier.

  Left: with a Code session open in the desktop app (one message sent so its
  CLI runs), an agent can do the rest from outside. Start a process parented
  to *that session's* CLI `claude.exe`, which inherits its job. Read that
  job's limit flags (kill on close? breakaway?), run the dev `SessionCli new`
  from it, and check the new terminal is not a member of the job. Quitting
  the app is the final proof, if the user allows it.
- **Prompts from the inbox and from links.** The morning brief writes its queue
  from a sandbox, and a link can be clicked from anywhere; letting either
  carry a prompt widens what they can make a session do, so decide the limits
  first.

## `new --trust` missed the trust prompt in a never-opened folder

Seen 2026-11-09 from a Remote Control session (guelph-sidewalks-layer)
starting one in guelph-bike-layer, a repo no Claude session had ever opened:
- `new --in <bike> --prompt-file <brief>` sat on "Do you trust this folder?";
  it reported "had not registered after 30s".
- The same with `--trust` said "No trust prompt appeared within 30s" and
  "had not registered after 30s". The process was alive, no registry entry,
  no transcript, no `hasTrustDialogAccepted` in `~/.claude.json`: the
  prompt was up and was missed. That launch had `--chrome --remote-control`,
  so startup may run past 30 s.
- `new --in <bike> --trust` with no prompt printed nothing at all, exit code
  not checked, and left the prompt up again.
- `trust <pid>` answered "No session matches": trust resolves against the
  registry, and a session at the trust prompt has not registered, so the one
  moment `trust` is needed is the one it cannot address.

The workaround was writing `projects["C:/Users/kk/Code/guelph-bike-layer"]
.hasTrustDialogAccepted = true` into `~/.claude.json` and launching again;
`new --prompt-file` then registered and started. To look at: a longer or
screen-driven wait for `--trust`, `trust` taking a pid or folder for an
unregistered terminal, and why the no-prompt launch printed nothing. Or have
`--trust` just set the flag before launching, since the user asked for it.

## Elsewhere, from the same review (2026-11-08)

Not Sky work; noted here so they are not lost. Each wants its own session.

- **Family knowledge in one place** (address family first). The same facts
  sit in 3–4 project memory stores and drift. Move shared facts to a doc in
  `address-importer-friend`, `@import` it from each city repo's CLAUDE.md,
  keep memory for what is local to one project.
  **Done 2026-11-09:** `address-importer-friend/FAMILY.md` (+ `docs/knowledge/`),
  imported by the engine and 17 city checkouts (not Oakville); duplicated
  memories removed from the engine, Toronto and Guelph stores; the
  out-of-tree import is approved per project in `~/.claude.json`.
- **Fan-out cost.** Decided 2026-11-09: gate only, no model tiering. The week
  to 11-08 split about half subagents (all Opus, mostly ontario-osm grading
  batches), and ~54 agents died mid-task on the 5-hour limit.
  `SessionCli spawn-gate` is built (warn 85%, refuse 95%; README "Spawn
  gate"); shipped in v1.16.0 and wired as a `PreToolUse` hook
  (`Agent|Workflow`) on 2026-11-09. Done.
