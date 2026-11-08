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

## `/clear` leaves a stale title and state

`/clear` is the most used slash command (63 times). After it, the session
keeps a title and declared state that describe work it has moved on from. A
`SessionStart` hook matching `clear` could reset the declared state and ask
for a new name.

## "Done" checked by a hook, not just declared

`stop-check` only checks that a state was declared. Discuss a per-repo check —
build or tests pass before a session may declare `exhausted`/`needs-read` —
the way the Claude Code docs and Mitchell Hashimoto turn must-hold rules into
hooks.

## Park projects on purpose: design + catalogue review

Of 160 repos under `~/Code`, 40 of the 80 outside any family had no commit in
30+ days, and 24 of 25 `experiment-*` repos are idle (runner paused since
09-20). Design a status per project (active / parked / done), where it lives,
and how Sky or the mind palace shows it; then review the catalogue with the
user.

## Know whether the user is asleep (Apple Watch)

The schedule in `~/.claude/CLAUDE.md` says asleep from 00:00, which is the
aim; prompts often run to 02:00. Instead of guessing from the clock: an iOS
Shortcuts personal automation on Sleep Focus on/off (the watch's sleep
schedule drives it) posts `asleep`/`awake` to a small endpoint; SessionCli
reads it, and the scheduler, `stop-check` and push notifications respect it.
Open: where the endpoint lives (a Cloudflare Worker + KV is the obvious one;
the PC itself is not reachable from the phone), and whether "awake" should
also come from a late prompt.

## `new --prompt`: start a session that already knows its task

A session that spots work belonging elsewhere has no clean way to start it.
`new` opens an empty prompt; Claude Code's own "start a task Claude suggests"
cards are a desktop-app feature, and `claude --bg` lands in a worktree in
agent view, not a terminal tab Sky tracks. On 2026-11-08 the review session
launched six follow-ups by hand, and it took three tries: WMI will not start
the `wt.exe` alias (error 8), and the line that worked was
`Win32_Process.Create` → `cmd /c start "" wt.exe -w <window> new-tab ...
powershell -Command "claude '<prompt>' --name '<n>' --chrome --remote-control"`.

What the verb needs to get right, all learned that night:
- the prompt goes before the flags (`--remote-control` would swallow it as
  its name);
- quoting: a `;` is a Windows Terminal tab separator, quotes end the
  `-Command` string — pass the prompt via a file or `-EncodedCommand`;
- launch outside the caller's job object, or the session dies with it;
- `--window <name>` so a batch lands in one window of its own;
- confirm by pid in `~/.claude/sessions/`, not by name: one of the six
  registered under its derived name (`sky-session-claude-6f`) first and was
  missing from `live` by name until it renamed itself;
- return the session id — with a prompt there is one at once.
Then `inbox` `new` and `link --new` can carry a prompt too, so the morning
brief can queue a session with its task.

## Elsewhere, from the same review (2026-11-08)

Not Sky work; noted here so they are not lost. Each wants its own session.

- **Family knowledge in one place** (address family first). The same facts
  sit in 3–4 project memory stores and drift. Move shared facts to a doc in
  `address-importer-friend`, `@import` it from each city repo's CLAUDE.md,
  keep memory for what is local to one project.
- **Fan-out cost.** The user likes fanning out and will pay for it; discuss
  how to keep it from eating the weekly limit (it hit the limit 2026-11-08).
