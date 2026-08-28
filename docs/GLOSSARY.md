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

## Runtime

Everything above is about a session file. This is about the process that writes
one — a separate axis, and the one where a single word was quietly covering two
unrelated things.

| Term | Meaning | Not to be confused with |
|---|---|---|
| **Harness** | The Claude Code process a session runs in — what has a build, a pid and a terminal. | The **harness turn** above, which is a *record* that process injects. Same tooling, different noun. |
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

- **A stale harness is invisible unless it is in the registry.** `--stale` reads
  live sessions, and a host publishes no session of its own (see
  `BridgePointer`), so hosts go stale unseen. The signature is on the process
  itself: an updater renames the running binary to `claude.exe.old.<timestamp>`
  so the new build can take the name, so any harness reporting that image has
  been overtaken at least once (`ClaudeInstall.IsClaudeProcess`).
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
