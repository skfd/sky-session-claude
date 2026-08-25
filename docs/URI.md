# `skysession://` — links that reference and start sessions

A link is worth building when the thing holding the link cannot run an executable. That is
the whole justification, and it is why this document is short: everything already on this
machine calls `SessionCli.exe` directly and needs none of this.

The shape below was settled in session `18a96f99` (2026-08-22) and then deferred rather than
dropped — the file queue (`SessionCli inbox --run`) was built first because it reaches a
phone and a link does not. The requirement conflicts between the two were resolved on
2026-08-25; where they disagreed, the decision and its reason are recorded below rather than
left to whichever file someone read last.

## What changed since it was designed

The old plan needed a host-side task to render the brief into a local `brief.html` so there
would be a page with no sanitizer to click in. **That step is gone.** The brief already
writes `morning-briefs/morning-brief-YYYY-MM-DD.html` on disk every morning, and its
behaviour is governed by `morning-briefs/brief-spec.md`, in git.

So the producer side is an edit to a spec file, not new infrastructure.

## Two halves of one brief

The brief is read twice: in the Cowork chat first, at 7am, often on a phone; then as the
HTML file at the desk. Neither surface serves both reads.

| Surface | Result |
|---|---|
| `morning-brief-*.html` opened in Chrome on this machine | **Works.** One consent dialog per origin, then never again. |
| The Cowork chat that produced the brief | Dead grey text. Markdown sanitizers allowlist `http`/`https`/`mailto`. |
| A phone | Nothing to receive the tap. No Windows handler exists there. |
| GitHub markdown | Stripped, same allowlist. No README badges. |
| Windows Terminal ctrl+click | Works, after one "this isn't a standard link" confirm. |

**The links are the desk half. The inbox is the phone half.** They are not competing
designs and neither replaces the other; they are two writers ending at the same verbs.

## The verbs

Three, mirroring the CLI vocabulary:

```
skysession://resume/<id>      reopen the session in its terminal. Acts immediately.
skysession://done/<id>        tick it off. Writes a mark, opens nothing.
skysession://new?in=<path>    start one in a folder. Confirms first.
```

`done` was not in the original three and earns its place: it is how a loose end gets
*finished* from the brief without reopening anything, it is the most harmless verb here — a
disposition mark, reversed by `undone` — and it is what makes the brief a triage surface
rather than a list of things to reopen.

`show` was in the original three and is dropped. Raising the window with a card selected is
what the app is already for; in a brief it is a link that does nothing.

`fork` and `restart` stay absent. They are the ones where a bad link costs something, and
neither has a use in a brief.

An unknown or malformed verb refuses and says why. There is no permissive default.

## Decisions where the two designs disagreed

**A click acts immediately; it does not go through the inbox.** The handler calls the same
typed entry points the app uses, so a terminal opens in about a second. Routing the click
into `commands.json` would give one execution path and one audit trail, but the safety rules
do not live in the inbox — they live in the verbs underneath it, which is the inbox's own
design principle. Calling the verb directly inherits the same refusals, so the queue would
buy a log and cost a click that appears to do nothing for up to five minutes.

**Links never expire.** The queue refuses anything older than `--max-age` because a machine
that was off for a week must not act on last week's decisions. A link is not a queued
decision: it is a pointer, and a `skysession://resume/<id>` pinned in `TODO.md` or in a code
comment beside a half-done migration only works if it outlives the brief that produced it.
Resuming a settled session costs a terminal.

**Every session this app opens carries `--remote-control`.** The branch's `ClaudeLaunch`
does this unconditionally; main still adds it only when the session already had it. Both
compile, so git cannot see the disagreement — it is settled here in the branch's favour,
because a session opened in a terminal nobody was watching is precisely the one that wants
reaching from a phone. `RestartPolicy.ResumeCommand` is corrected when the branch merges.

**`new?in=` is allowlisted against configured roots, not a hardcoded path.** The inbox
allows folders that already have sessions in them — a list nobody maintains, but one that
cannot open a fresh clone, which is the strongest use case `new` has. The roots live in
`%APPDATA%\sky-session-claude\settings.json` beside the marks, read through the same sidecar
that surfaces a warning rather than failing silent, and default to `~/Code` when the file is
absent.

## The security shape

`ms-msdt` (Follina), `steam://`, `zoommtg://` and a long line of Electron argument-injection
bugs are all one bug: a page navigates to a scheme, Windows appends the attacker's string to
`shell\open\command`, and it executes. Eight rules keep this boring, and none are optional:

1. **Never build a command line from URL text.** Parse the URI in-process, then call the
   same typed entry points `SessionCli` uses. No string concatenation into `cmd`/`powershell`.
2. Register as `"%1"` and re-validate with `Uri.TryCreate`. Reject quotes, newlines, `&`, `|`.
3. **Allowlist the folder.** `in=` must resolve under a configured root, exist, and be a
   repo. Refuse UNC, `\\?\`, device paths, and anything that escapes a root by traversal.
4. **`--trust` is never reachable from a link.** Folder trust is Claude Code's actual
   security boundary, and a link is not the operator typing.
5. **No prompt payload that submits.** If `&prompt=` is ever supported it prefills the input
   box and stops there. A link that opens a session *and* sends it a prompt is RCE with extra
   steps.
6. **Confirm before `new`, and only before `new`.** A fresh agent in a folder asks first.
   `resume` and `done` act on their own: their damage ceiling is a terminal opening, or a
   mark that `undone` reverses, and the brief is a one-click surface or it is pointless.
7. `HKCU\Software\Classes\skysession` — per-user, matching the
   `%LOCALAPPDATA%\Programs\SkySessionClaude` install, no admin. Written by `publish.ps1`,
   removed on uninstall.
8. **Route into the running window.** Single-instance handling takes the URL rather than
   starting a second app.

Rule 6 is the one that moved, and it is worth being honest about the cost: once Chrome is
told "always allow", a page you browse can fire `resume` and `done` without asking. That is
accepted. Neither destroys anything, and a confirm dialog on the verb clicked every morning
would make the whole feature not worth having.

## Build order

1. **`SessionCore/SessionUri.cs`** — parse and validate. Returns a typed request or a
   refusal with a reason; knows nothing about windows or registries. This is the entire
   security boundary, so it is the part that gets the hostile inputs in tests.
2. **`SessionCore/LinkRoots.cs`** — the configured roots, read through `JsonSidecar` from
   `%APPDATA%\sky-session-claude\settings.json`, defaulting to `~/Code`.
3. **`SessionCli link <id>`** — the producer, so something emits links before anything
   consumes them. `link --done <id>` and `link --new <path>` for the other two.
4. **App route** — `App.xaml.cs` single-instance path takes the URL; `new` shows the confirm
   dialog; then the existing resume / done / new paths run unchanged.
5. **`publish.ps1`** writes the `HKCU` key; the uninstall path removes it.
6. **`brief-spec.md`** — Needs-attention items emit `skysession://resume/<SessionId>` and
   `skysession://done/<SessionId>`; non-session items that live in a repo emit
   `skysession://new?in=<path>`. The ids are already in `sessions-unfinished.json`, and the
   brief writes no links at all today, so this is purely additive.

Steps 1–5 are this repo. Step 6 is `morning-briefs`, takes effect on the next 07:00 run, and
needs no desktop-app edit because the spec is authoritative for behaviour.

## Verify before building

Open a recent `morning-brief-*.html` in Chrome and click a hand-written `skysession://x`
link. The handler does not exist yet, so the only thing this proves is that Chrome offers to
launch something rather than treating the href as inert — which is the fact the whole plan
rests on.

## The inbox comes second

`worktree-force-resume` carries `SessionCli inbox --run` and is still unmerged; main has
advanced past it, so the fast-forward that branch was left expecting is now a real merge —
five conflicting files, of which `Commands.cs` is the only substantial one, plus the
`--remote-control` policy decided above.

That merge is wanted: it is the phone half, and the brief is read on a phone first. It is
not a dependency of anything here. This build lands first because it is smaller, has nothing
in its way, and settles one of the merge's decisions on the way past.
