# Implementing project state

[`docs/PROJECT-STATE.md`](PROJECT-STATE.md) is the design and the *why*. This is the build
order and the traps. **Steps 1 to 4 are built** — what each turned out to cost, and where it
came out different, is recorded under each one. Steps 5 and 6 are not, and the plan's reason
for holding them still holds.

Read the design first. This file does not restate the vocabulary; it says what to touch, in
what order, and what will bite.

## Facts already established — do not re-derive

- **There is no todo state on disk.** `~/.claude/todos` does not exist, and no session file
  under `~/.claude/projects` carries a `TodoWrite` record — the tool is not in this harness's
  toolset. Checked, not assumed. If it ever appears, it is a free source for "work remaining"
  and step 2 gets much smaller.
- ~~**`SessionInfo` has no turn uuid.**~~ It does now: `LastPromptUuid`, the uuid of the last
  genuine operator prompt. This was called the one thing that would stop the build mid-flight,
  and it nearly was — for a reason a step further in than the one written here. See step 2.
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

### 1. The derived fold, and `list --projects` — done

Pure policy, like `RestartPolicy`, `ClosePolicy` and `StandbyPlan`: the caller supplies the
scan and the dispositions, and gets back a roll-up. `ProjectState.cs` in `SessionCore`, no
filesystem access of its own, unit-testable without a session file.

With no declarations in existence yet, every project comes out `broken`, `runnable`,
`undeclared`, `quiet` or `abandoned` — not `blocked`, see below. That is the point: the fold
is worth having on its own, and shipping it first shows which of the declared states are
actually needed before any protocol is written. It did exactly that, twice.

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

Settled: **instead of** them, each project row carrying its `sessionIds` as the way back
down. `--project`, `--search` and `--unfinished` narrow the rows; `--limit` caps them. `--top`
and `--newest-per-project` are **refused**, and this was nearly the trap the parser one is —
they narrow the scan rather than the rows, so the fold would run over part of a project and
report the answer as the project's, successfully and wrongly.

Two things came out different from what is above:

- **The fold may not derive `blocked`.** This file expected `blocked` from `waiting-you` with
  no declaration; the design argues at length that the continuation half of that claim is not
  in the file, so it reads `undeclared`. See the note under *Fold order* in the design.
- **A live session is never `broken`.** Not in either document, and the first run of
  `list --projects` found it: a session mid-turn ends its file on a `tool_use`, which is what
  a session that died mid-tool also leaves, so this repo reported itself as a corpse off the
  session doing the reading. The fold takes one runtime fact now — `Liveness` — and being
  there is enough to rule broken out, because not every harness publishes busy or idle.

### 2. `SessionCli state`, and the sidecar — done

**Prerequisite, and it was worse than this said.** The parser had to surface the uuid — but
of the last **operator prompt**, not the last real turn. An agent declares mid-turn: the tool
call, its result and the closing message are all records written after the declaration, so
anchored to the last turn every declaration the convention produces is stale before its
session ends. `SessionFileFields.LastPromptUuid` is that anchor; tool results and harness
records are real turns and do not move it, an interrupt does.

Then the store itself is a copy, not an invention: `JsonSidecar<...>` beside
`DispositionStore`, its own mutex prefix, the same three rules (never write cached state,
never truncate in place, never fall back on a parse error — set the bad file aside and
report it). `DispositionStore` is the worked example; follow it line for line.

New verb wiring is three places, not one: the dispatch switch in `Cli.cs`, the `HelpText`, and
`Args.RejectUnknown` in the new command.

Settled for now: **no**, `state` is not in `InboxFile.Allowed`. A declaration is a claim
about a session the brief is not inside, which is a different thing from the operator marking
one done, and adding it later is purely additive.

`state blocked` refuses without `--note`. A blocked state without a named blocker rots, and
the moment of declaring is the only moment anyone knows who the blocker is.

### 3. `docs/GLOSSARY.md` owes entries — done

The design introduces vocabulary the term authority does not have: **declaration** (the third
axis, next to derived Status and declared disposition), and the states `quiet`, `exhausted`,
`undeclared`, plus **fold**. Add them there, with the two laws stated as flatly as the
disposition law is. The glossary is what stops these words drifting; a design doc alone will
not hold them.

### 4. The convention, and measuring it — half done

The line is in `~/.claude/CLAUDE.md`, under *What happens next here*, carrying the full path.
**The measurement is the next thing to do, and nothing else should start before it.**
Measure: what fraction of sessions in a week actually carry a live declaration. If it is low, the `Stop` hook is next — it cannot know the *state*, but it can
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

## What is left to look at

Two of the three arguable calls are settled above. The third is still open, and now there is
something to look at:

- **The fold order puts `broken` above `blocked`, and `undeclared` below `runnable`.** The
  second half is the one to revisit. `undeclared` was ranked as an oddity; with `blocked`
  declared-only it is the common case for a project holding a question, which now sorts below
  a project that merely needs waking. On this machine that is two projects under one, and it
  reads wrong. Leave it until the convention has been running long enough to say how often a
  question is actually declared.
