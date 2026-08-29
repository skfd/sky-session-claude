# Implementing project state

[`docs/PROJECT-STATE.md`](PROJECT-STATE.md) is the design and the *why*. This is the build
order and the traps. Nothing here is built yet.

Read the design first. This file does not restate the vocabulary; it says what to touch, in
what order, and what will bite.

## Facts already established — do not re-derive

- **There is no todo state on disk.** `~/.claude/todos` does not exist, and no session file
  under `~/.claude/projects` carries a `TodoWrite` record — the tool is not in this harness's
  toolset. Checked, not assumed. If it ever appears, it is a free source for "work remaining"
  and step 2 gets much smaller.
- **`SessionInfo` has no turn uuid.** It carries `LastActive`, `LastTouched`,
  `PreviousActive`, `Status`, `Recap` — no identifier for the last real turn. Law 2 (a
  declaration expires at the next real turn) has nothing to compare against today. See the
  prerequisite under step 2; this is the one thing that will stop the build mid-flight if it
  is discovered late.
- **`RealCwd` is the fold key.** `Cwd` is never empty — a file with no recorded cwd gets
  `SessionInfo.UnknownCwd`, which slugs into a folder name if read as a path (there was once a
  session called `unknown-cwd-not-found-in-session-file-b9`). `RealCwd` is the field that
  answers "is the folder known". A session with none belongs to no project.
- **`--self` works from inside a session.** `CLAUDE_CODE_SESSION_ID` survives the process
  ancestry; `rename --self` already relies on it. `SessionCli` is not on PATH, so any
  convention that asks an agent to run it must carry the full path.
- **Rename never reaches an SDK or desktop-app session.** Irrelevant to this feature except
  as the reminder that not every session can be driven from outside — a declaration is written
  *by* the session, which is exactly why it does not have this problem.

## Build order

### 1. The derived fold, and `list --projects`

Pure policy, like `RestartPolicy`, `ClosePolicy` and `StandbyPlan`: the caller supplies the
scan and the dispositions, and gets back a roll-up. `ProjectState.cs` in `SessionCore`, no
filesystem access of its own, unit-testable without a session file.

With no declarations in existence yet, every project comes out `broken`, `blocked`,
`runnable`, `undeclared` or `quiet`. That is the point — the fold is worth having on its own,
and shipping it first shows which of the declared states are actually needed before any
protocol is written.

Traps:

- **`--projects` must go into the `Switches` set in `Cli.cs`.** It takes no value, and the
  comment on that set spells out what happens otherwise: the parser cannot tell
  `list --projects foo` from `list --status complete`, so the flag eats the next bare word and
  the command reports success while doing the wrong thing.
- **The default output is load-bearing.** `Commands.List` already argues this for the `Hosts`
  array: the twice-daily dump the morning brief reads must keep meaning what it always did.
  Project rows are opt-in the same way, in their own array, absent unless asked for.
- **Sessions with no `RealCwd`** join no project. They stay in the session rows; they do not
  make a project row and must not create a phantom one named after the sentence.
- **Abandoned is skipped and Done folds as Settled** — the design says so, and this is where
  it actually has to be written. An abandoned session must never be what makes a project read
  `undeclared`.

Open decision: does `--projects` emit project rows *alongside* the sessions, or *instead of*
them? Alongside matches `--hosts` and keeps one scan answering both questions; instead-of is a
smaller payload for a phone brief that only wants the roll-up. Not decided.

### 2. `SessionCli state`, and the sidecar

**Prerequisite:** `SessionFileParser` must surface the uuid of the last real turn, and
`SessionInfo` must carry it. Without it law 2 cannot be implemented and the declaration store
is a set of claims with no expiry — which the design says is worse than no claims at all. Do
this first, not after the store.

Then the store itself is a copy, not an invention: `JsonSidecar<...>` beside
`DispositionStore`, its own mutex prefix, the same three rules (never write cached state,
never truncate in place, never fall back on a parse error — set the bad file aside and
report it). `DispositionStore` is the worked example; follow it line for line.

New verb wiring is three places, not one: the dispatch switch in `Cli.cs`, the `HelpText`, and
`Args.RejectUnknown` in the new command.

Open decision: should `state` join `InboxFile.Allowed`, so the phone brief can queue it? The
list is deliberately short and its comment is a warning — "guessing an action wrong costs
someone's terminal". A declaration is a claim about a session the brief is not inside, which
is a different thing from the operator marking one done. Wants thought, not a default.

### 3. `docs/GLOSSARY.md` owes entries

The design introduces vocabulary the term authority does not have: **declaration** (the third
axis, next to derived Status and declared disposition), and the states `quiet`, `exhausted`,
`undeclared`, plus **fold**. Add them there, with the two laws stated as flatly as the
disposition law is. The glossary is what stops these words drifting; a design doc alone will
not hold them.

### 4. The convention, and measuring it

A line in `~/.claude/CLAUDE.md` asking agents to declare before they stop, carrying the full
path to `SessionCli`. Then measure: what fraction of sessions in a week actually carry a live
declaration. If it is low, the `Stop` hook is next — it cannot know the *state*, but it can
write `undeclared` with the turn uuid, which distinguishes "this agent never reports" from
"this agent reported and things moved on".

Do not build the hook before the measurement. The convention may be enough, and a hook that
fires on every session is not free.

### 5. App group headers

Grouping goes through `RowsView`, which is already an `ICollectionView` — add a
`GroupDescription` on project and a header template carrying the rolled-up state. No new
collection, no second scan.

Leave this until the CLI has been lived with, so the headers show the states that turned out
to carry weight rather than all seven.

### 6. The tray and title split

`MainViewModel.cs:527` is where the count and the window title are set today — one number,
"still on the hook". Splitting it into *N need you* / *N just need waking* lands here and in
`CountIcon`/`TrayIcon`.

Last, deliberately. The tray icon is a single glyph that already gives up at `99+`, and what a
split looks like there is a design decision that should be made while looking at real numbers
from steps 1–4, not guessed now.

## What to check before starting

Nothing blocking. The design has three arguable calls, all recorded in
[`docs/PROJECT-STATE.md`](PROJECT-STATE.md) rather than settled: the fold order putting
`broken` above `blocked`, whether `--projects` replaces or accompanies the session rows, and
whether the inbox may queue a declaration. Any of them can be decided when the code makes the
consequence visible.
