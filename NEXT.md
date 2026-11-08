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

## Elsewhere, from the same review (2026-11-08)

Not Sky work; noted here so they are not lost. Each wants its own session.

- **Family knowledge in one place** (address family first). The same facts
  sit in 3–4 project memory stores and drift. Move shared facts to a doc in
  `address-importer-friend`, `@import` it from each city repo's CLAUDE.md,
  keep memory for what is local to one project.
- **Fan-out cost.** The user likes fanning out and will pay for it; discuss
  how to keep it from eating the weekly limit (it hit the limit 2026-11-08).
