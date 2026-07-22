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
session, and the scanner never sets it.

| Term | Meaning |
|---|---|
| **Disposition** | What the operator decided to do about a session. Independent of Status. |
| **Abandoned** | The one disposition: "this session is genuinely unfinished, and I'm not going back to it." |

The rule that keeps the two axes honest: **abandoning does not change Status.**
An abandoned `cut-off` session stays `cut-off` and stays **Unfinished** — the
classifier's verdict was correct, the operator is only overriding what to *do*
about it. Never fold Abandoned into `complete`; `complete` means the agent
finished, which is the opposite of what abandoned records.

Abandoned sessions are hidden by default and revealed by the **Show abandoned**
filter, which renders them struck through — the strikethrough is what
distinguishes the operator's judgment from the classifier's.

Dispositions live in `abandoned.json` under `%APPDATA%\sky-session-claude`,
keyed by `SessionId`. They are deliberately **not** in `sessions.json`, which is
a regenerated scan artifact and would erase them on every scan.

## Note on close-outs vs Status

A **close-out** does not currently change Status. In practice the agent almost
always replies to "thank you" ("you're welcome"), so the last real turn is an
**agent turn** and the session is already `complete`. A close-out would only
flip a verdict (`waiting-agent` → `complete`) in the rare case where the
operator thanks the agent and exits before the agent replies — which did not
occur in the last 50 sessions reviewed.
