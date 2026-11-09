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

## Smoke tests for the tooling's own regressions

The tooling now causes more friction than Claude misreading the user. Recent:
"sessions no longer report if they are blocked and other statuses" (10-12),
Remote Control hosts silently down after Standby (10-08), stray backup files
in `~`. Cover status reporting, host liveness and cleanup with tests that
would have caught those.

Decided 2026-11-09: **gate + A + C**, no sandboxed exe smoke (B).

- **Gate.** Nothing ran `dotnet test` before a release; `publish.ps1` now
  must, and stop on failure.
- **A, in `dotnet test`.** The 10-12 bug was wiring, not logic:
  `DeclarationStore.ReloadIfChanged` was tested since 08-29, `MainViewModel`
  never called it, and SessionApp has no tests. Move the live tick's body into
  SessionCore with a test that a state declared after start shows on the next
  tick. Add a temp-home fixture that fails on any file a test leaves unlisted
  (the backups class).
- **C, `SessionCli doctor` on the live machine.** Hosts dying days after a
  sweep is `claude rc`'s own doing; no pre-release test sees it. The keeper
  watches hosts; doctor watches the keeper: every standby project has a host,
  Sky wrote a heartbeat lately (it writes none today; `standby.log` only logs
  decisions), Sky's shown states match `declarations.json`, no stray
  `.bak-*` in `~`. Scheduled before 07:00 and ~17:30; registering the task
  needs the user's OK in chat.

## `/clear`: wire the hook once the verb ships

Sky now reads a conversation that ended in `/clear` as settled, and flags a
session still answering to the name `/clear` copied over (`TitleInherited`).
Left: once a release ships `SessionCli clear-start`, add it to
`~/.claude/settings.json` as a `SessionStart` hook with matcher `clear`, so
the agent is told its name was inherited and renames if the subject changed.
Not before: the hook points at the installed CLI, which does not know the
verb yet. Optional later: show `TitleInherited` on the card (dimmed name).

## "Done" checked by a hook, not just declared

`stop-check` only checks that a state was declared. Discuss a per-repo check —
build or tests pass before a session may declare `exhausted`/`needs-read` —
the way the Claude Code docs and Mitchell Hashimoto turn must-hold rules into
hooks.

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
installed CLI at the next release.

Reviewed with the user 2026-11-09: 106 of ~156 repos set aside (40 parked,
47 done, 19 automated; `SessionCli project` lists them with notes). The
`skfd` duplicate went to the Recycle Bin (its one unmerged branch is in
`skfd-profile`), `obsidian`'s notes were committed (it stays active), and
`families.json` gained `address` and `layers` (mind-palace `45cda09`).

Left: release, so the installed CLI and tray have `project`; the palace
reading `Standing` and drawing parked fog apart from forgotten fog;
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
- **Job objects.** Every launch (`new`, `resume`, `fork`) goes through
  `Process.Start`, so a session started from the desktop app is in the app's
  job and dies when the app quits. Launching outside the job (as publish.ps1
  now does for Sky itself) would fix all of them at once.
- **Prompts from the inbox and from links.** The morning brief writes its queue
  from a sandbox, and a link can be clicked from anywhere; letting either
  carry a prompt widens what they can make a session do, so decide the limits
  first.

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
  gate"). Left: release it, then add the `PreToolUse` hook to
  `~/.claude/settings.json`. Not before: the installed build exits 2 on an
  unknown verb, which would block every spawn.
