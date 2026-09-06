# Glossary

Agreed terminology for talking about sessions and how they're classified. Use
these terms in code, comments, commits, and discussion so we stop conflating the
raw JSON role strings (`"user"` / `"assistant"`) with what they actually mean.

## Actors

| Term | Meaning | Not to be confused with |
|---|---|---|
| **Operator** | The human who runs the session and types prompts. | The JSON `"user"` role, which is broader (see *turns* below). |
| **Agent** | Claude, the AI doing the work. | The JSON `"assistant"` role string; also the parent-vs-subagent sense elsewhere. |

## Structure

| Term | Meaning |
|---|---|
| **Session** | The logical conversation — one row in the grid. |
| **Session file** | The `.jsonl` artifact on disk that records a session. In code: `SessionFileParser`, `SessionFileCache`, `SessionFileFields`. |
| **Record** | One line of the session file (one JSON object). |
| **Last real turn** | The last record that survives the pre-filter and actually drives classification — i.e. the final meaningful record after skipping attachment/mode/snapshot noise. |

## Turns (what a record represents)

A record's JSON `type`/role doesn't tell the whole story; these names do.

| Term | JSON shape | Meaning |
|---|---|---|
| **Operator turn** | `user` role carrying typed **text** | Something the operator actually typed. |
| **Tool-result turn** | `user` role carrying a **`tool_result`** | Machine-generated; the operator did not type it. A tool-result turn as the last real turn means the session died between a tool result and the agent's next turn (→ `cut-off`). |
| **Harness turn** | `user` role whose text is injected by the tooling: `<system-reminder>`, `<command-name>/clear`, `<task-notification>`, etc. | Not typed by the operator. The classifier skips these as noise, so the last real turn stays the last genuine operator/agent exchange rather than reading as `waiting-agent`. |
| **Agent turn** | `assistant` role with real text/tool_use | Something the agent said or did. |
| **Error/limit record** | `assistant` role flagged `<synthetic>` or `isApiErrorMessage` | System-injected, not real agent text. Classifies to `limit` or `error`. |

## Classification

| Term | Meaning |
|---|---|
| **Status** | The classification output for a session (the README column, the `SessionStatus` enum). Always **derived** from the session file. |
| **Close-out** | A terminal operator turn that acknowledges rather than requests — "thank you", "all good", "perfect". Closes the conversation without asking for anything. |
| **Unfinished** | Collective term for every Status except `complete` (`waiting-you`, `waiting-agent`, `cut-off`, `limit`, `error`, `interrupted`). These are the amber rows. |

### Status values (derived)

| Status | Last real turn | Means |
|---|---|---|
| `complete` | Agent turn, no trailing `?`, not cut off | Agent finished; nothing pending. |
| `waiting-you` | Agent turn ending in `?` | Agent asked the operator a question. |
| `waiting-agent` | Operator turn (or harness turn) | Operator spoke last; agent owes a reply. |
| `cut-off` | Agent turn stopped at `tool_use`/`max_tokens`, **or** a tool-result turn | Session died mid-work. |
| `limit` | Error/limit record naming a usage/spend/weekly/session limit | Hit a usage limit. |
| `error` | Any other error/limit record | API or other error ended it. |
| `interrupted` | Operator turn containing `[Request interrupted by user` | Operator interrupted the agent. |

## Disposition (operator judgment)

Everything above is **derived** — the scanner reads the session file and decides.
A **disposition** is the opposite: it's what the *operator* decided about a
session, and the scanner never sets it. A session carries at most one.

| Term | Key | Meaning |
|---|---|---|
| **Disposition** | | What the operator decided to do about a session. Independent of Status. |
| **Abandoned** | **X** | "This session is genuinely unfinished, and I'm not going back to it." |
| **Done** | **D** | "This session is finished — whatever the classifier says." |
| **Settled** | | Collective term for a session with nothing left to do: Status `complete` *or* disposition Done. This, not `complete` alone, is what **Hide completed** hides. It is not the same as *off the hook*: the title's open count and `list --unfinished` skip the Abandoned too, which are unfinished by Status and still not coming back. |

The rule that keeps the two axes honest: **a disposition never changes Status.**
An abandoned `cut-off` session stays `cut-off`; a Done `waiting-you` session
stays `waiting-you`, and its card still says so. The classifier's verdict about
the *file* was correct — the operator is only overriding what to *do* about it.
Never fold either disposition into `complete`, which means the agent finished.

Done exists because the classifier can be right about the file and still wrong
about the work. An agent that lands the change and then asks "want me to push?"
leaves the session `waiting-you`; an operator who hits Esc once the work is in
leaves it `interrupted`. Both are finished in every sense the operator cares
about, and Done is how they say so without pretending the file ended otherwise.
The gap between "the file ends mid-sentence" and "the work is done" is exactly
what these marks make measurable.

Abandoned cards are hidden until the **Show abandoned** filter reveals them, and
render struck through and dimmed. Done cards follow **Hide completed** — they
are settled, so by default they drop out of the list too — and carry a green
tick on the title line. The strikethrough and the tick are what distinguish the
operator's judgment from the classifier's.

Dispositions live in `dispositions.json` under `%APPDATA%\sky-session-claude`,
keyed by `SessionId`: `{"<id>": "abandoned" | "done"}`. They are deliberately
**not** in `sessions.json`, which is a regenerated scan artifact and would erase
them on every scan. The pre-1.9 store, `abandoned.json` — a bare array of ids —
is migrated on first read and then left alone.

Two processes write the file: the app on a keystroke, and `SessionCli` on an
agent's behalf. So a write is a reload-merge-replace under a machine-local mutex
rather than a dump of what the writer loaded at startup, and the new file is
moved over the old one rather than truncating it in place. A store that will not
parse is set aside as `dispositions.json.corrupt` and reported — never answered
with the legacy `abandoned.json`, which would silently revert every Done mark to
the pre-1.9 abandon list.

## Declaration (what the session said)

A third axis, and the last one. Status is **derived** — the scanner reads the file
and decides. A disposition is **declared by the operator**. A **declaration** is
declared by the *session*: an agent saying what happens next here, which is the one
question the file cannot be read for.

The gap is real and no sharpening of the classifier closes it. "Has work left" versus
"genuinely finished", "answer, and three more things happen" versus "that was the last
question", "there is a report here worth reading" versus "nothing to see" — all three
are claims about the *future*, and the last real turn only describes the past. Both
sides of each pair end on an agent turn with a `?`. Someone has to say so, and here the
someone who knows is the agent, not the operator.

| Term | Meaning |
|---|---|
| **Declaration** | What a session said about what happens next. Written by `SessionCli state`, never by the scanner and never by the operator. A session carries at most one. |
| **runnable** | Work is queued and needs no decision — wake it and walk away. |
| **blocked** | Held by the operator, and work follows once they answer. Refused without a `--note` naming the blocker: a blocked state without one rots. |
| **needs-read** | Over, but there is a report here worth the operator's eyes. |
| **exhausted** | Over, and there is nothing here — whatever the last turn looks like. |

The two laws, in the same shape as the disposition law above:

1. **A declaration never changes Status.** A session declared `exhausted` that ends on
   a question stays `waiting-you`, and its card still says so. The classifier's verdict
   about the *file* was correct; the agent is only reporting what happens next. Only the
   project's state moves.
2. **A declaration expires when the operator says something new.** It records the uuid
   of the last operator prompt at the moment it was written, and the claim is dead once
   the session has moved past it. Agents forget to re-declare, and a confident wrong
   `exhausted` is worse than no claim at all.

The anchor is the last **operator prompt**, not the last turn, and the difference is
the whole thing working. An agent declares mid-turn — the tool call, its result and the
closing message are all records written after it — so anchored to the last turn every
declaration would be stale before the session ended. Tool results and harness-injected
records are user records and real turns, so they move `LastActive`; they are not the
operator speaking, so they leave the anchor alone. An interrupt does move it: reaching
over and stopping the agent is exactly the kind of event a claim about what happens
next should not survive.

Out of band rather than as a trailer on the final message, for the reason `SessionCli
done` already is: prose is fragile to parse and this parser would be load-bearing, a
trailer lives in the transcript forever and is copied into every fork of it, and it
would be written by exactly the actor whose closing prose already misleads the
classifier. A session that ends badly — cut off, out of tokens, killed — never writes
its trailer, and those are the sessions whose state you most want.

Declarations live in `declarations.json` beside `dispositions.json`, keyed by
`SessionId`: `{"<id>": {"state": ..., "note": ..., "atTurn": ..., "at": ...}}`. Same
write discipline, and one rule of its own — a state word the store does not understand
is dropped rather than kept as nothing, because an entry that decides nothing reads as
a session that reported when nothing was reported.

## Project state

Everything above answers "what is this conversation doing". This answers the question
a level up: **what is this whole project waiting on**. It is a **fold** — one row per
project folder, rolled up from every session that ran there, and a project is as urgent
as its most urgent session:

`broken` > `blocked` > `needs-read` > `runnable` > `undeclared` > `exhausted` / `quiet`
> `abandoned`

| Term | Meaning |
|---|---|
| **Fold** | The roll-up itself: sessions in, one project row out (`ProjectFold`). Pure — the caller supplies the scan, the marks and the declarations. |
| **broken** | Something died mid-work. Revive it; there is no decision to make. |
| **undeclared** | Something is unfinished and nobody said what it needs. Not a missing value — its own answer, and the common one until agents declare. |
| **quiet** | Nothing was pending to begin with: every session here is Settled. |
| **exhausted** | An agent looked at unfinished-looking work and said it is over. |
| **abandoned** | Every session here is crossed out. The operator said no. |

`blocked`, `needs-read`, `runnable` and `exhausted` carry the meanings the declaration
table gives them.

Three sources answer, in a fixed order, and the order is the design: the **operator's**
disposition, then the **agent's** declaration while it still stands, then the
**classifier's** Status. An Abandoned session is skipped outright — it is unfinished and
not coming back, so it must never be what makes a project read `undeclared` — and a Done
session folds as Settled. A declaration is consulted *before* the settled check, or
`needs-read`, which is a claim about a session that already looks finished, would be
unreachable.

**`quiet` and `exhausted` are not the same answer.** `quiet` is derived: nothing was
ever pending. `exhausted` is declared: an agent looked at unfinished work and said it
is over. They land the operator in the same place by opposite routes, and the day one
of them is wrong you will want to know which one you were reading.

**The classifier may not call a project `blocked`.** A session waiting on the agent is
`runnable`, because waking it is right whatever follows. A session waiting on the
operator is `undeclared`: "answer, and three more things happen" and "that was the last
question" are the same file, and guessing between them is the mistake this whole axis
exists to prevent. `blocked` is reachable only through a declaration. Collapsing
`undeclared` into anything quieter would make silence read as "nothing to do here",
which is the exact direction in which a mistake costs you work you forgot about.

One runtime fact enters the fold, and only one: **whether the process is still there**.
A session taking a turn ends its file on a `tool_use`, which is the same last record a
session that died mid-tool leaves — the classifier is right to call both `cut-off`, and
nothing in the file separates them. A session known to be mid-turn reads `runnable`;
and a live session is never `broken` at all, whatever its file ends on, because broken
means "the process is gone, put it back" and there is nothing to put back. That second
rule has to stand on its own because not every harness says whether it is mid-turn — one
running under the SDK or answering a phone publishes no busy or idle — so being there is
all there is to go on. What such a session reads as instead is `undeclared`, which is
exactly true: something here is unfinished and nobody said what it needs.

Sessions with no recorded cwd join no project. `Cwd` is never empty — it holds
`SessionInfo.UnknownCwd` — so folding on it would invent a project named after the
sentence, which is how a session once ended up called
`unknown-cwd-not-found-in-session-file-b9`. `RealCwd` is the field that answers whether
the folder is known.

`SessionCli list --projects` emits these rows *instead of* the session rows, each
carrying its `sessionIds` as the way back down. The fold always runs over the whole
scan: a project state folded over the sessions that survived `--status waiting-you` is
not a smaller answer, it is a wrong one.


## Runtime

Everything above is about a session file. This is about the process that writes
one — a separate axis, and the one where a single word was quietly covering two
unrelated things.

| Term | Meaning | Not to be confused with |
|---|---|---|
| **Harness** | A running Claude Code process: a build and a pid, usually a session and a terminal too. Not always — a host runs sessions rather than being one, and an SDK harness has no terminal to drive. | The **harness turn** above, which is a *record* that process injects. Same tooling, different noun. |
| **rc** | Short for Remote Control, and always the **host** shape: "an rc of a project" is a `claude rc` server running in that folder. Also a verb — to *rc* a repo is to start one there, which is `standby --in <path>`. A message from the operator that is nothing but a project name is that request, not a topic. | A **bridged session** (`claude --remote-control`): reachable from a phone, but one conversation with no way to open a second, so it is not an rc. And never a **release candidate** — this project has no such build; between releases the version carries `-dev`. |
| **Build** | The CLI version a harness runs, like `2.1.250`. Fixed when it launches: Claude Code updates in place and a running harness keeps the build it started with until it restarts, which is why a dozen terminals start nagging at once. |
| **Installed build** | The newest version present under `~/.local/share/claude/versions` — what every staleness question compares against (`ClaudeInstall`). |
| **Stale** | A live harness whose build is behind the installed one. The opposite is **current**. | Anything about the session file. A stale harness's conversation is not old, unfinished or damaged; only its code is behind. |

Stale is a fact about a process, not about a session — which is what lets it
apply where there is no session at all. Remote Control comes in two shapes and
both are harnesses: a **host** (`claude rc`) is a server that spawns sessions on
demand and has none of its own, while a **bridged session**
(`claude --remote-control`) is one interactive session with the phone attached.
A host ages exactly like a terminal does, and "stale host" is the right way to
say so.

Two things that follow, both easy to get backwards:

- **A host is stale by its image, not by its version.** It publishes no registry
  entry (see `RemoteControlHosts`), so there is no build on it to compare. What says
  so instead is the rename: an updater moves the running binary to
  `claude.exe.old.<timestamp>` so the new build can take the name, so a harness
  reporting that image has been overtaken at least once
  (`ClaudeInstall.IsSuperseded`). Both verbs know this: `list --stale` reports
  the stale hosts in their own `Hosts` array — their own, because a host has no
  status, no context and no session id to put in a session's — and
  `restart --stale` sweeps them, judging each by the conversations it is
  serving (`HostRestartPolicy`).
- **A stale host still makes current sessions.** A spawned session takes
  whatever build is on disk at spawn time rather than inheriting its host's, and
  then ages on its own clock from there. So staleness never propagates
  downwards, and a host being behind says nothing about the conversations under
  it.

## Note on close-outs vs Status

A **close-out** does not currently change Status. In practice the agent almost
always replies to "thank you" ("you're welcome"), so the last real turn is an
**agent turn** and the session is already `complete`. A close-out would only
flip a verdict (`waiting-agent` → `complete`) in the rare case where the
operator thanks the agent and exits before the agent replies — which did not
occur in the last 50 sessions reviewed.
