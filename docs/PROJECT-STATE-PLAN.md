# Implementing project state

[`docs/PROJECT-STATE.md`](PROJECT-STATE.md) is the design and the *why*. This is the build
order and the traps. **Steps 1 to 5 are built** — what each turned out to cost, and where it
came out different, is recorded under each one. Step 5 did not exist when this was first
written; the 2026-09-13 reading turned it up, and taking it out moved the headline number
from 34.1% to 74.5%. Steps 6 to 8 are not built. Step 6 is new and is where the work goes
next; the reasons for holding 7 and 8 still hold.

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

### 4. The convention, and measuring it — built; deciding reading taken 2026-09-13

The line is in `~/.claude/CLAUDE.md`, under *What happens next here*, carrying the full path.

**It points at `dist/SessionCli.exe`, not the installed CLI, and that is deliberate.** The
verb ships in no release yet, so the installed binary under `%LOCALAPPDATA%` answers
`Unknown command: state` and every agent that follows the line fails. `dist` is refreshed by
`publish.ps1 -SkipInstall` (or a `dotnet publish` of the CLI project alone), which never
touches the installed app or a running Sky window — so the convention can be iterated on
without a release. **Move the line to the installed path at the next release**; the CLAUDE.md
entry says so itself, which is the copy that will actually be in front of whoever does it.
**The measurement now exists, and has a first reading.** The instrument is
`measure-declarations.ps1` at the repo root, over two additive fields the session rows grew
for it: `Declared`, the claim while it still stands, and `DeclaredStale`, a claim the
operator has prompted past. Stale and silent stay separate buckets because they are the two
different failures the hook paragraph below distinguishes. Sessions mid-turn are excluded
from the denominator — they have not reached their declare point — but a harness under the
SDK publishes no busy or idle, so some of those land in silent anyway, including whichever
session runs the script.

First reading, 2026-08-31, 2.5 days in: **13 sessions in the window, 5 declared, 2 stale,
6 silent — 38.5% live.** Read the silent six before trusting the number: one was the
measuring session itself (SDK, mid-turn, uncountable), one ended in `error` and could never
have declared, one had no recorded cwd and belongs to no project, and one predates most
agents having seen the convention line at all. The honest silent count among sessions that
*could* have declared is closer to two. Small denominator; one row moves the number by
eight points.

**The deciding reading, 2026-09-13** (eight days later than the plan asked for, which changes
nothing — more window, not less): **341 sessions, 340 judged, 116 declared, 30 stale, 194
silent — 34.1% live.**

That number is not a reading of the convention. It is a reading of the denominator, and the
denominator is wrong. One project, `battle-agents`, contributes 166 of the 341 rows and 157
of the 194 silent. Take it out and the same window says **108 declared, 29 stale, 37 silent —
62.1% live** (108/174). The convention is landing on roughly two sessions in three, not one
in three.

`battle-agents` is not a project with 157 silent sessions. It is a project that **uses Claude
as a library**, and every call it makes writes a file the scanner counts as a session. Their
prompts are what that looks like from the inside — *"Reply with ONLY strict JSON, no markdown
fence"*, *"You are the slop filter for a personal index…"*. Nobody sat in those. There is
nothing to go back to, nothing to declare, and no operator the declaration would be for.
This is not a measurement problem that a better script fixes; see the new step 5 below.

The second exclusion worth naming and *not* worth netting out: fourteen `experiment-2026-…`
folders, one per day, each a single silent session whose last prompt reads *"Final turn. It
is 12:44. Stop after this — the process is killed at 18:00."* A scheduled routine, checked
rather than inferred from the folder name. Excluding those too would say 67.9%, and that is
where this stops — subtract exclusions until the number pleases and it has stopped being a
measurement. **62.1% is the figure of record.**

**The stale bucket is the other half and has its own number: 29 of 174, about one in six.**
Those agents declared once and the operator prompted past it without a re-declare. A `Stop`
hook does not fix them — law 2 already catches a stale claim and falls the project back to
`undeclared`, which is correct and also means the hook would be draining the wrong bucket.
`poi-categories` is the extreme case: 1 declared, 6 stale. The CLAUDE.md line already says
"declare again before you stop again"; it is not landing.

So the hook decision does not decide itself, and the plan never defined "low". 62% among
sessions that could comply, with a distinct 17% who complied once and then went quiet, is not
the lopsided number that authorises building it — and the bucket it would drain cannot be
counted correctly until step 5 exists. Held, deliberately, and handed to the operator rather
than settled here.

**Corrected by step 5, and the correction changes the question.** With unattended sessions out
of the denominator the same window reads **74.5%** — 111 declared, 28 stale, **10 silent**.
The convention is working. The `Stop` hook was aimed at the silent bucket, and that bucket is
now ten rows out of 149; stale is 28, nearly three times as many. Building the hook would be
spending the effort on the smaller of the two failures, and on the one that is already
shrinking on its own. **Stale is the failure to work on**, and law 2 already surfaces it
correctly — what is missing is anything that prompts a re-declare. Still the operator's call;
the number now points somewhere different than it did three paragraphs ago.

### 5. Operator sessions and programmatic ones — done

The measurement turned this up, but it is not about the measurement. Every roll-up in this
design reads the same session list, so every one of them inherits the problem:
`list --projects` reports `battle-agents` as one project holding 273 rows that will read
`undeclared` forever, the tray count carries them, and the `undeclared`-below-`runnable`
worry in *What is left to look at* is this same thing magnified. A project whose sessions
nobody will ever declare on is not a project in the sense this design means, and there is
currently no way to say so.

What the file records, checked rather than guessed:

- **`entrypoint`**, on every `user` record: `cli` for a terminal, `claude-desktop` for the
  desktop app, `sdk-cli` for a session driven through the SDK. Every one of the 40
  `battle-agents` files sampled says `sdk-cli`.
- **`promptSource`**, per prompt: `typed` or `sdk`. All 39 sampled `battle-agents` prompts
  say `sdk`.

**Neither is sufficient, and this is the trap.** This very session is `sdk-cli` with
`promptSource: "sdk"` and has an operator typing into it — the same two values
`battle-agents` carries. Worse, a session can change entrypoint mid-file: this one holds 58
`cli` records and 131 `sdk-cli`, because it was resumed under a different harness. So the
signal exists, it is genuinely there in the file, and it does not by itself separate "a human
is driving an SDK harness" from "a program is calling Claude as a library". Whatever
distinguishes them is a step further in, and finding it is the work.

Do not patch `measure-declarations.ps1` to exclude `battle-agents` by name in the meantime.
The script's denominator is wrong for a reason the whole feature shares, and hardcoding the
one project that exposed it would hide the finding and keep the bug.

**What it turned out to be: `origin`.** A prompt a person sent carries
`origin: {"kind": "human"}`; the only other kind in the corpus is `task-notification`, the
harness telling a session its background work finished. One human record anywhere in a file
settles it for good, which is `SessionFileFields.HasOperator`, and the glossary's word for the
other case is **unattended**.

Three things had to be checked before trusting it, and all three held:

- **Terminals carry it.** The worry above was that `origin` was an SDK-only field and the
  rule would call every `cli` session unattended — the commonest harness on this machine.
  Sampled terminals carry 8, 3 and 3 human origins. It is broader than `promptSource` too:
  this session holds 3 human origins against 1 `typed` prompt.
- **It is not version-gated.** Human origins appear from 2.1.217 through 2.1.270 and
  no-human files span the same range, so a file without one is silent about its operator on
  purpose rather than because it predates the field. This was the real risk — a format
  boundary would have made every old session unattended.
- **The false-positive audit is clean.** All 32 no-human sessions active in the measurement
  window outside `battle-agents` are scheduled `experiment-2026-*` runs or spawned one-shots
  with zero or one prompt. No operator session is wrongly excluded.

Two more that were checked because they would have bitten at release rather than in the
tests:

- **The parse cache does not persist.** `SessionFileCache` is an in-memory
  `ConcurrentDictionary` keyed on write time, length and context window. Had it been a file,
  every entry written before this build would deserialize `HasOperator` as `false` and the
  fold would drop every session on the first run after an update — a bug that shows up only
  on the installed app, only once, and looks like the feature is broken.
- **The phone counts as a person.** A prompt typed into a `claude rc` host from the phone
  arrives as `promptSource: queued` through a bridge, which is neither of the shapes the rule
  was sampled on. Checked directly: a session with 51 `bridge-session` records and 4 queued
  prompts carries 5 human origins. Standby is the user's actual workflow, so this one had to
  be verified rather than inferred from the audit.

**The re-read, same window, 2026-09-13: 150 sessions, 149 judged, 111 declared, 28 stale,
10 silent — 74.5% live**, with 191 unattended excluded. The convention was never landing on
one session in three; it lands on three in four. And **silent collapsed from 194 to 10**,
which changes what the held decision is about: see step 4.

`list --projects` came out the way the design wanted. `battle-agents` now folds over its 10
real sessions and reads `blocked`, carrying `Unattended: 263` so the library use is stated
rather than hidden. All fourteen `experiment-2026-*` folders produce no row at all; the
`experiment-runner` repo that *contains* the routine still does, on the one session a person
had there. Corpus-wide the field says 347 of 729 session files were never sat in — close to
half of what the scanner had been calling sessions.

One consequence, deliberate and worth having written down: a scheduled routine that dies on a
usage limit now disappears from `list --projects` rather than reading `broken`.
`experiment-2026-09-13` did exactly that today. "What am I on the hook for" and "did last
night's job crash" are different questions and the second one is not this verb's — but if it
ever needs answering, this is the line that traded it away.

### 6. Re-declaring — the failure that is left

The 74.5% reading leaves two buckets, and this is the big one: **28 stale against 10 silent**.
An agent declares once, the operator asks for something else, the agent does it and stops
without declaring again. Law 2 catches it correctly and the project falls back to
`undeclared`; nothing asks for the re-declare.

**Checked first, because it would have changed the answer: law 2 is not over-expiring.** The
worry was that trivial follow-ups were killing good declarations. The prompts that expired
the 24 `complete` stale rows are real work — *"push it"*, *"xtb it"*, *"add to xrm-ribbon
backlog this idea with details pls"*, *"why is syncthing like that"*. Two of the 24 are
close-outs (*"all good, just surpised it was easy"*, *"leave it, enough for now"*). The rest
are the agent simply not declaring again.

**Do not implement close-out detection for those two.** It has been suggested once and will
be suggested again, so: it means classifying prose for intent, which is the guess this design
refuses everywhere else — the fold may not derive `blocked` for exactly this reason, and the
glossary deliberately keeps close-outs out of Status. Two rows in 149 does not buy an
exception, and a false positive is worse than the stale it fixes: it keeps a dead declaration
alive and the project reads confidently wrong.

**What does work — verified, not assumed.** A blocking `Stop` hook can ask. Run in a scratch
project on 2026-09-13: the hook exits 2 with a reason, the harness feeds the reason back, and
the agent's next act was to try to declare (it was denied by permission mode, which is its own
lesson for the real thing). This also corrects something this file said earlier — the hook was
written off as able only to "write `undeclared`", which is true of a hook that reports and
false of one that asks. A hook that asks fixes stale and silent with one mechanism.

**The trap it walked into, now fixed.** The hook's feedback is written to the session file as
a **plain-string `user` record carrying no tag the harness-text filter recognises** — so it
read as an operator prompt and moved `LastPromptUuid`. A hook that asks an agent to declare
would have moved the anchor it was asking the agent to declare against, and every prompted
declaration would have been stale the moment it was made. The record does carry `isMeta:
true`, and the parser was reading `isMeta` nowhere. It does now: a meta record is never a
prompt, which is the rule `LastPromptUuid` already documented, stated by a field instead of
guessed from a prefix. `LastPromptUuidTests` pins the exact record shape.

This was worth fixing on its own. 541 `isMeta` user records exist across 51 projects, and
three sessions today anchor a declaration on one — *"Continue from where you left off."*, a
skill preamble, a `/context` dump. Small now; every stop would have been one once a hook
existed.

**`SessionCli stop-check` — built.** It reads the hook's JSON on stdin, exits 0 when
`stop_hook_active` is true, and otherwise compares the sidecar's `AtTurn` against the file's
`LastPromptUuid`, exiting 2 with a reason only when no current declaration stands. The logic
lives here so the hook stays dumb plumbing; `--session <id>` and `--dry-run` run the same
check by hand, which is the only way to see what a hook would do without stopping a session
to find out.

Two rules it is built around, both easy to get wrong:

- **Exit 0 on every failure.** A missing session, an unreadable store, a line of JSON that
  will not parse — all of them stand down and say so on *stdout*, because stderr on a
  non-zero exit is the block reason and a diagnostic is not one. A hook that errors must never
  be the thing that stops somebody working. The measurement is worth something; nobody's
  evening is.
- **One ask per stop.** `stop_hook_active` is the harness saying the agent is only still going
  because this hook blocked it. Answering that with another block is how a session never ends.
  If the agent ignores the ask, the state stays undeclared and the next reading says so —
  honest, and better than an agent stuck in a corridor.

It also stands down on unattended sessions, which falls out of step 5: there is nobody to
declare for, and blocking a `-p` run would hold work no operator is waiting on.

Verified against real sessions: a stale one asks (exit 2, reason on stderr), one carrying a
current declaration is silent, an unattended one is silent, and garbage on stdin, an unknown
id and empty input all stand down at exit 0.

**What is left**, and it needs the operator's eyes before it lands:

1. One line in `~/.claude/settings.json` under `Stop`. **Show it to the operator first** — it
   fires on every session on this machine, and the existing `SessionStart` hook is the shape
   to copy.
2. Re-read afterwards. If stale and silent both collapse, step 4's question is finally closed.

### 7. App group headers

Grouping goes through `RowsView`, which is already an `ICollectionView` — add a
`GroupDescription` on project and a header template carrying the rolled-up state. No new
collection, no second scan.

Leave this until the CLI has been lived with, so the headers show the states that turned out
to carry weight rather than all seven.

### 8. The tray and title split

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

  Step 5 cleared the way for this: `undeclared` no longer doubles as "a project full of
  library calls", so the rank now decides between real things and the question can actually be
  answered. Still open, and now genuinely answerable.
