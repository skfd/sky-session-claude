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

Amended 2026-11-20 (`400b7c1`): record-only still holds at the declaration —
the claim is written whatever the check says — but a standing `exhausted` /
`needs-read` now settles its session out of `--unfinished` and the app's
default views, and a FAILED outcome blocks that settling (the claim stands,
the session stays on the hook). One narrow read of the record as a hold,
reversible in `Declaration.Settles` if the measurement says otherwise.

## Loose ends from settle-by-declaration (2026-11-20)

- `close --finished` (`ClosePolicy`) still judges by Status + disposition
  only: a live terminal whose session declared exhausted reads "idle, but
  unfinished (cut-off)" — offered, never swept. Teaching it
  `Declaration.Settles` means threading the declaration into its callers.
- A needs-read project card opened via `OpenProject` with Hide completed on
  now hides the very session that holds the report (it is settled). Maybe
  OpenProject should drop the filter, or the card should link `StateFrom`.
- Both settle rules (declared-over, unattended — `0df1e6b`) reach the
  installed CLI, the Stop hook and the scheduled export only at the next
  release; until then the installed exe still lists ~129.
- Seen 2026-11-20: `state --self` run from the repo's own dev-built
  SessionCli.exe records a false FAILED when the CLI is stale — the check's
  `dotnet test` tries to rebuild SessionCli and the running exe locks its
  own SessionCore.dll (MSB3021). With a FAILED check now blocking settling,
  that false fail keeps the session on the hook. Rebuild first, or run the
  installed exe; maybe the check should build with a different output path.

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
- **Job objects.** Shipped in v1.17.1 (2026-11-09, `OutsideJob`). A desktop-app Code session's CLI sits in a
  private job (flags `0x800`) that the app ends on quit. When the launcher
  descends from the app, terminals go through `Win32_Process.Create` instead.
  Proven live: after the app restarted, the terminal opened by the dev `new`
  from inside that job survived, and the in-job controls died. Package
  identity does not reach an agent's shell, so the ancestry check is what
  fires. Cowork runs in a VM and is not affected. `SessionCli doctor` has a
  `launch` line saying which way and why.
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

## `new --trust`: record the trust up front, as standby does

Seen 2026-11-11: kk-init ran `SessionCli new --in <fresh ~/Code folder>
--trust --name ...`. It opened the terminal, then reported "No trust prompt
appeared within 30s" and "It had not registered after 30s". The session
never came up, presumably stuck on the trust dialog. `rc.ps1` (standby) on
the same folder then worked first time, with `TrustRecorded: true`.

The reason: `new --trust` still watches the terminal for the dialog and
answers it (the helper shared with `trust`, Commands.cs ~2276). Standby has
written `hasTrustDialogAccepted` through `SessionCore/ClaudeTrust.cs` since
v1.11.0. Make `new --trust` call `ClaudeTrust` before launching (same backup,
same one-property text edit), and keep watching the screen only as a
fallback if anything. kk-init depends on it for every new project.

**Done 2026-11-17:** `new --trust` now calls `ClaudeTrust.Grant` before the
terminal opens and reports "Folder trust: ..." in its message. The screen
watch runs only if the grant fails. Proven live: kk-init's task-beholder
launch had sat at the dialog from 21:40. The rebuilt CLI on the same folder
recorded trust and registered on the first poll. Two loose ends from the
2026-11-09 item above are still open: `trust` cannot address an unregistered
pid, and the old binary's run printed nothing at all. The registered name
also came back `task-beholder-0f` despite `--name 'Task Beholder'`.

Followed up the same evening:
- **The name is a race, not a bug.** A minute later `~/.claude/sessions/44076.json`
  read `"name": "Task Beholder", "nameSource": "user"`. `new` caught the
  first registry write, which still had the derived name, before the CLI
  applied `--name`. It's cosmetic and fixes itself. If it ever matters,
  `Registered` could re-read once when `nameSource` is `derived` and a name
  was asked for.
- **"Printed nothing" was most likely the caller timing out.** The old slow
  path is a 30 s trust wait plus a 30 s register wait. The 2026-11-17 caller
  gave up at 60 s and its captured output came back empty, though the
  process later exited 0. With trust recorded up front, that path is now
  only the fallback.
- **`trust` on an unregistered pid** matters much less now that
  `new --trust` never meets the dialog. Leave it unless something else
  starts sessions in untrusted folders.
