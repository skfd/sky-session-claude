# Implementing the naming design

`docs/NAMING.md` is the design and the *why*. This is the build order and the traps.

**Built, all ten steps**, with one part of step 4 narrowed — see fork naming below. Kept as
written, because the traps it named are the ones that bit and this is the record of what was
expected. Five things turned out otherwise:

- **Rename reaches `cli` sessions only.** The desktop app and the SDK publish the pipe, take
  the message and do nothing. `docs/NAMING.md` is corrected; the background pass is gated on
  the entrypoint so it does not spend ten seconds of timeouts per tick on sessions that will
  never comply, while the verb still tries anything, so a future build starts working on its
  own.
- **A `.key` file is a small JSON object**, and the auth line wants its `peerToken` field.
  Sending the file authenticates as nobody, silently — three failed smoke tests before the
  file's shape was checked rather than assumed.
- **The oracle is behind `rename --ask`, not on the poll.** The design calls the blocking
  call tolerable only while it stays rare, and nothing has self-named yet, so an automatic
  sweep today would be a minute of waiting and a bill nobody asked for. Renaming is free and
  happens unasked; this is not free, so it does not.
- **`NameOrigin` ranks the oracle above `aiTitle`**, which reads backwards against the source
  order in the design. That list is cheapest-viable-first — the order worth *spending* in —
  while the ladder is quality order, and an `aiTitle` is written in a session's first ten
  minutes and never revisited.

- **Fork naming covers `--at-prompt` only.** `SessionForker` authors the whole file, so
  writing `fork: <the prompt it branched at>` into it is no new authority and the store
  record is exact. `fork --tip` is the CLI's own `--fork-session`: the id does not exist
  until the CLI invents it, minutes after this process has gone, so there is nothing to name
  and nothing to record. A tip fork keeps the parent's title until something renames it.

Both step-8 checks came back as this file guessed: `CLAUDE_CODE_SESSION_ID` survives the
ancestry, so `--self` works from inside a session; `SessionCli` is not on PATH, so the line
carries the full path.

## Already done (do not redo)

- `docs/NAMING.md` — every decision, all twelve taste answers, worked examples, measured
  costs. Read it first.
- One Sky window per desktop (`SingleInstance.cs`, commit `a546e5f`). This is what makes
  background renaming safe: no leader election needed, there is one actor.
- `publish.ps1` relaunches the app it closed (`a4ef754`).
- `restart --stale` crash: was never a live bug, `6b7d877` had fixed it and the installed
  binary predated it. Published.

## Facts already established — do not re-derive

- **Pipe rename protocol**, verified end to end from PowerShell against a live `cli`
  session. Newline-delimited JSON to the named pipe in the registry's
  `messagingSocketPath`. First line `{"type":"auth","token":"<peerToken>"}`, where
  peerToken comes from `~/.claude/sessions/<pid>.<hash>.key`. Then
  `{"type":"control","action":"rename","name":"…"}`. Unauthenticated lines are dropped and
  the connection closed.
- **Every live session publishes that pipe** — desktop app and SDK included, all nine
  checked. Rename reaches sessions restart cannot.
- **A pipe rename records `nameSource` absent**, exactly like `--name`. Nothing in the
  registry distinguishes Sky's names from yours. Hence the sidecar.
- **Naming writes a `custom-title` line into the transcript** — both `--name` and the pipe.
  It even creates the file for a session that had none.
- **`SessionFileParser` takes the last `custom-title`**, so a later rename supersedes an
  earlier one. No delete-lines authority is needed to repair.
- **`claude -p --model haiku`**: 8.9s as-is, 5.3s with `--strict-mcp-config --mcp-config
  '{"mcpServers":{}}'`, $0.020 per name, leaves a 16–22KB session file that shows up in
  `list`. Prompt must go on **stdin** (variadic flags eat a trailing prompt argument).
  `--output-format json` returns `session_id`, which is what makes cleanup targetable.
- **Build output paths differ**: `dotnet build` writes `bin/Release/net10.0-windows/`,
  `publish.ps1` writes `.../win-x64/`. Launching the wrong one wastes a test cycle.

## Build order

Pure first, then wired, then external. Commit per component.

### 1. Decision layer (pure, unit-tested)

- `SessionName` additions: `RepoOf(cwd)` stripping `.claude/worktrees/<name>` to the repo
  folder; `Compose(subject, cwd)` → `Subject — repo`; `Floor(sessionId, cwd)` → `repo-<id2>`;
  `IsFloor(name, …)`; sentence-case helper. `Tidy`/`MaxLength`/`Quote` already exist.
- `NameStore` — the provenance sidecar. Follow `DispositionStore` exactly: named
  `Local\` mutex, reload-merge-replace, write-beside-and-move. Same
  `%APPDATA%\sky-session-claude` directory.
- `NamePolicy` — **the single decider**. Everything else executes a name it is handed.
  Inputs: the session row, its live entry, the store, and the collision set (live sessions
  only). Output: a name plus why, or nothing.

Two rules that are easy to get subtly wrong:

- **The collision override only fires when there is a subject to write.** Three sessions
  reading `vagabond maps` get replaced by what they were about — but the two with no
  content keep the name you typed. Replacing a chosen name with `vagabond-map-69` is
  strictly worse than the collision.
- **The floor is only for sessions with no chosen name to lose.**

### 2. Pipe client

- `LiveSession.MessagingSocketPath` parsed from the registry; key file found by globbing
  `<pid>.*.key` in the same directory.
- `SessionRenamer` — connect, auth, send, report. Thin; the protocol is proven.

### 3. CLI `rename`

`rename <id> [name] --self --dry-run`. With no name, ask `NamePolicy`.

**The invariant that is the entire fix: every Sky name-write records to the sidecar in the
same operation.** `--name` on a launch, a pipe rename, the app's background pass — all of
them. A write that skips the sidecar recreates the masquerade bug.

### 4. Launch paths — the gap most likely to be missed

`RestartPolicy.ResumeCommand` and `SessionInfo.NamedCommand` currently decide names
themselves (`IsChosen ? live.Name : For(...)`). If they do not go through `NamePolicy`,
**every restart keeps re-freezing Sky's old names and the sidecar buys nothing.**

Keep `RestartPolicy` pure: it already takes `title` as a parameter, so extend that pattern
and pass the *decided name* in. Otherwise the store leaks into `RestartPolicy` and every
assertion in `RestartPolicyTests` needs store setup.

While in that file: the comment at `RestartPolicy.cs:110` claims `--name` never reaches the
transcript. That is false and proven false. Fix it.

Fork naming: `fork: ` plus the `Tidy`'d prompt it branched at. Inbox gets naming for free
through these paths.

### 5. Slug guard at the *resolution* point — the other easy miss

`SessionName.RealTitle(custom, ai, id, cwd)`, used where `BuildRow` resolves the title —
not inside `NamePolicy` only. Otherwise the app still *displays* `xrm-librarian-1c` as a
title and `NamedCommand` keeps rewriting it. One resolution point fixes display, launch
paths and policy together. Sidecar is primary; the shape check is the fallback for history
written before the sidecar existed, and can misfire on a name you genuinely typed that
happens to look like `repo-XX`.

### 6. App background pass

On the existing poll. Single-instance guarantees one actor, so no locking beyond the store's.

### 7. `NameOracle` (`claude -p`)

Only when self-naming did not happen. Prompt on stdin, which frees the flags — pass
`--disallowedTools` so the call is pure text. **Once the CLAUDE.md line is installed an
oracle call will read it too; a tool-less call cannot act on it.** Scratch cwd, delete by
the reported `session_id`. Strip backticks, then `Tidy`.

### 8. The CLAUDE.md line — verify two things first

Drafted in `NAMING.md`, ~120 chars. Before installing it, check:

1. **How `IsSelf` detects the current session**, and whether it survives the ancestry
   `SessionCli ← shell ← claude` when invoked from a Bash tool. Test from inside a real
   session before installing the line.
2. **Whether `SessionCli` resolves on PATH at all.** Every call in the design session used
   the full `%LOCALAPPDATA%\Programs\SkySessionClaude\SessionCli.exe` path. If it is not on
   PATH the drafted line is a no-op as written and needs the full path.

The line goes in the user's *global* `~/.claude/CLAUDE.md`, which is their personal config —
say so when it lands.

### 9. Repair pass

Re-rename the sessions carrying a Sky slug as `custom-title`: `xrm-librarian`,
`xrm-plugin-step-codegen`, `ontario-address-changes`, and `sky-session-claude-93` (whose
transcript exists only because the protocol smoke test created it). The parser takes the
last `custom-title`, so a correct rename supersedes the slug.

`ontario-address-changes-b9` has no `aiTitle`, so recomputing hits the floor and rewrites
the same slug. Harmless — but say so rather than reporting it as repaired.

### 10. Finish

- Update `~/.claude/skills/sky-session/SKILL.md` with the `rename` verb. It is how future
  sessions learn this CLI, and that skills directory is a git repo — commit there too.
- Full test run (`SessionCore.Tests`, 193 green before this work).
- One `publish.ps1` at the end. It now closes and relaunches the app itself.

## Standing constraints

- **Renaming is the only thing Sky may do unasked**, because it is the only act that cannot
  lose anything. Restart can drop a pending approval, `trust` can close a session, answering
  a question puts words in your mouth. Use this to decide anything this plan does not cover.
- Sentence case, plain words, `Subject — repo`, 60 characters via `Tidy`.
- The blocking oracle in the restart path is a deliberate choice, tolerable only while step 7
  stays rare. Keep it gated behind "self-naming did not happen".
