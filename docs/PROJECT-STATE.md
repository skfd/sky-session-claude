# Project state

`docs/GLOSSARY.md` answers "what is going on in this session". This answers the question a
level up: **what is going on in this project** — and specifically the four answers a person
standing in front of the list actually wants to tell apart.

- It has work queued and only needs waking.
- It is held by me, and it will keep going once I answer.
- It is over, but there is something here I should read.
- It is over, and there is nothing here.

Design only. Nothing below is built. The vocabulary, the two laws and the storage shape are
the decisions; the build order is a sketch at the end.

## The line the glossary already draws

Sessions have two axes and the glossary keeps them from touching: **Status** is *derived* —
the scanner reads the file and decides — and a **disposition** is *declared* — the operator
decides, and it never rewrites Status. Project state falls on both sides of the same line,
and the halves need different machinery. Getting them confused is the way this feature goes
wrong: a classifier that guesses at intent, or a declaration that quietly overwrites a fact.

## What folds (derived, available today)

A project's derived state is a fold over the Status of every session that ran in its folder,
plus the runtime facts already on hand. No agent cooperation, no new file, and it works on
every session already on disk:

| In the project | The project reads as |
|---|---|
| any `waiting-agent` | something is queued — wake it |
| any `waiting-you` | held by the operator |
| any `error` / `limit` / `cut-off` | died mid-work; revivable with no decision to make |
| all Settled | quiet |

Alongside it, the runtime axis from the glossary — is anything live in this folder, is it
stale, is there a `claude rc` host — which `StandbyPlan` already computes per folder for its
own purposes.

## What cannot be folded

Three of the distinctions above are not in the file, and no amount of sharpening the
classifier will put them there:

- **Work remaining.** "Has autonomous work left" versus "genuinely finished" is a claim about
  the *future*. The last real turn describes the past.
- **Continuation.** "Blocked on you, and three more things happen once you answer" versus
  "blocked on you, and that was the last question" look identical from the outside. Both end
  on an agent turn with a `?`.
- **Whether the message matters.** "Exhausted, and there is a report here worth your eyes"
  versus "exhausted, nothing to read" is a judgment about the agent's own output.

The failure mode is already documented in practice: an agent lands the change and then asks
"want me to push?", so the last turn reads `waiting-you` on a session whose work is done.
That is why `Done` exists as an operator disposition. Project state hits the same wall one
level up, and the same answer applies — someone has to *say so*. The difference is that here
the someone who knows is the agent, not the operator.

Worth recording because it was checked rather than assumed: there is **no todo state on
disk** to lean on. `~/.claude/todos` does not exist on this machine, and no session file
under `~/.claude/projects` contains a `TodoWrite` record — that tool is not in this harness's
toolset. In a harness that has it, a pending todo list would be an agent-declared "work
remaining" artifact the scanner could simply read, and half of this document would be
unnecessary. It is not available, so the declaration has to be built.

## The vocabulary

Not a flat enum — those grow forever. Three orthogonal facts that compose:

| Fact | Values | Source |
|---|---|---|
| **Ball** | `agent` / `operator` / `nobody` | derivable; sharpened by declaration |
| **Continues** | does work follow once the ball comes back? | declared only |
| **Unread** | is there a report the operator should read? | declared only |

Which compose into the states:

| Ball | Continues | Unread | State | What the operator does |
|---|---|---|---|---|
| agent | — | — | **runnable** | wake it and walk away |
| operator | yes | — | **blocked** | answer, and it keeps going |
| operator / nobody | no | yes | **needs-read** | read it; then it is over |
| nobody | no | no | **exhausted** | nothing here — and an agent said so |
| — | — | — | **broken** | revive; no decision to make |
| — | — | — | **quiet** | nothing here — nothing was pending to begin with |
| — | — | — | **abandoned** | the operator said no |
| — | — | — | **undeclared** | something is unfinished and nobody said what it needs |

`broken`, `quiet` and `abandoned` are purely derived — from `error`/`limit`/`cut-off`, from
every session being Settled, and from the existing disposition. They are in the same list
because they are what the operator sees, not because they arrive the same way.

**The cell that looks missing is the important one.** Ball=`operator`, Continues=`no`,
Unread=`no` — "want me to push?" with nothing following and nothing worth reading — does not
appear because a declaration of `exhausted` is precisely the assertion that the ball is
`nobody`, whatever the file's last turn says. Law 1 below keeps this honest: the session's
Status stays `waiting-you` and its card still says so; only the project's state moves. That
one transition is most of what this whole document is for.

**`quiet` and `exhausted` are not the same answer.** `quiet` is derived — every session in
the folder is Settled, so nothing is pending and nothing was ever declared. `exhausted` is
declared — an agent looked at unfinished-looking work and said it is over. They land the
operator in the same place and arrive by opposite routes, and the day one of them is wrong
you will want to know which one you were reading.

**`undeclared` is a state, not a missing value.** It is the project with something unfinished
by Status and no declaration to explain it — the fold knows the ball is not `nobody` and
cannot say more. A project nobody ever reported on and a project reported as `exhausted` are
different situations, and collapsing them means silence reads as "nothing to do here" — the
exact direction in which a mistake costs you work you forgot about. Show it as its own thing.

### Fold order

A project is as urgent as its most urgent session:

`broken` > `blocked` > `needs-read` > `runnable` > `undeclared` > `exhausted` / `quiet`

The last two are equally not-urgent and are distinguished only for honesty, per above.

`blocked` under `broken` is arguable — only one of the two needs a human, which is a fair
reason to put `blocked` on top instead. It is ranked this way because a broken session is
cheap to fix and blocks everything behind it, while a question can wait for the operator to
be at the desk. Revisit it once there is something to look at.

Dispositions enter the fold the way they already enter the title's open count: an **Abandoned**
session is skipped entirely — it is unfinished and not coming back, so it must never be what
makes a project read `undeclared` — and a **Done** session folds as Settled, because Settled,
not `complete`, is what "nothing left to do" means here.

## How an agent declares it

**Out of band, not in the final response.** A structured trailer at the end of the assistant
message is the obvious idea and it is the wrong one, for four reasons:

- Parsing prose is fragile, and this parser would be load-bearing.
- The trailer is in the transcript forever, and gets copied into every fork of it.
- It is written by exactly the actor whose closing prose already misleads the classifier.
- A session that ends badly — cut off, out of tokens, killed — never writes its trailer, and
  those are the sessions whose state you most want.

The precedent is already in the repo: `SessionCli done` writes a disposition on an agent's
behalf, and the inbox is a second one-shot channel written by an agent with no chance to
retry. So:

```
SessionCli state runnable|blocked|needs-read|exhausted [--note "..."] [--self]
```

`--note` is the one line the operator reads on the card — *what* is queued, or *who* the
blocker is. A blocked state without a named blocker rots; a note is what stops it.

### The two laws

Mirroring the law that keeps Status and disposition honest:

1. **A declaration never changes Status.** A session declared `exhausted` that ends on a
   question stays `waiting-you`, and its card still says so. The classifier's verdict about
   the *file* was correct; the agent is only reporting what happens next.
2. **A declaration expires at the next real turn.** It records the uuid of the turn it was
   made at. If the session has moved past that turn, the claim is stale and the project falls
   back to the derived fold — and to `undeclared` where the fold cannot answer. Agents forget
   to re-declare, and a confident wrong `exhausted` is worse than no claim at all.

### Storage

A sidecar under `%APPDATA%\sky-session-claude`, keyed by session id, next to
`dispositions.json` and never inside `sessions.json`, which every scan regenerates:

```json
{ "<session-id>": { "state": "blocked", "note": "...", "atTurn": "<uuid>", "at": "<iso>" } }
```

Same write discipline as `DispositionStore`: more than one writer (an agent's `SessionCli`,
possibly the app), so reload-merge-replace under the machine-local mutex and move the new
file over the old one. A store that will not parse is set aside and reported, never silently
answered with an empty one — the failure mode `dispositions.json.corrupt` exists to prevent.

### Making it happen at all

A CLAUDE.md convention gets some sessions. A `Stop` hook gets every session, but the hook
cannot know the state — the most it can do is write `undeclared` with the turn uuid, which
is still worth having: it distinguishes "this agent never reports" from "this agent reported
and then things moved on". Start with the convention; add the hook if the convention proves
as leaky as it looks.

## Where it surfaces

Three places, all three wanted:

- **`SessionCli list --projects`** — one row per project folder: the rolled-up state, the
  note behind it, the counts underneath, and the runtime facts (live, stale, host). This is
  the machine-readable one and the one the phone brief and `inbox --run` would read. Build it
  first; it is where the model gets proved.
- **Project group headers in the app** — group the card list by project with the rolled-up
  state on the header, so the list answers the project question without being read
  card-by-card.
- **The tray count and window title** — today one number, "still on the hook". With this it
  can separate *N need you* from *N just need waking*, which is the difference between an
  evening at the desk and thirty seconds of walking the list. The icon is a single glyph, so
  what it shows is a decision to make when it is built, not now.

## Prior art

Nobody appears to do the project-level roll-up over agent sessions. Every piece of it exists
elsewhere:

- **Devin, and Cursor/Codex background agents** — an explicit "blocked, needs input" state
  distinct from running and from done. The closest existing thing to `blocked`.
- **LangGraph's `interrupt` / human-in-the-loop** — blocked-on-human as a first-class
  *resumable* state with the continuation stored, which is exactly "and it will keep going
  once you answer".
- **CI approval gates** — `queued` / `running` / `waiting-for-approval` / `failed` /
  `success`. `runnable` / `blocked` / `broken` map onto it almost exactly, and it is the best
  evidence available that this vocabulary can stay this small.
- **systemd unit states** — `active` / `exited` / `failed`: a tiny closed vocabulary that
  survived decades by refusing to grow. The model for saying no to a seventh state.
- **GTD's "waiting-for" and the kanban blocked column** — the human version, and the source
  of the rule that a blocked item without a named blocker rots.

## Build order (sketch)

1. The derived fold and `list --projects`, declarations absent — every project reads
   `runnable` / `blocked` / `broken` / `undeclared` / `quiet`. Pure, like `RestartPolicy` and
   `ClosePolicy`: caller supplies the scan, gets back the roll-up.
2. `SessionCli state` and the sidecar, with both laws and the expiry uuid.
3. The CLAUDE.md convention; measure how many sessions actually report.
4. App group headers, once the CLI has shown which states carry their weight.
5. The tray split, last — it is one glyph and the least reversible.
