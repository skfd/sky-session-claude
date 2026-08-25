# `skysession://` — links that reference and start sessions

A link is worth building when the thing holding the link cannot run an executable. That is
the whole justification, and it is the reason this document is short: everything already on
this machine calls `SessionCli.exe` directly and needs none of this.

The design below is not new. It was settled in session `18a96f99` ("Sky session protocol
design", 2026-08-22) and then deferred rather than dropped — the file queue
(`SessionCli inbox --run`) was built first because it reaches a phone and a link does not.
This file is that design, written down, with the one fact that has changed since.

## What changed since it was designed

The old plan needed a host-side task to render the brief into a local `brief.html` so there
would be a page with no sanitizer to click in. **That step is gone.** The brief already
writes `morning-briefs/morning-brief-YYYY-MM-DD.html` on disk every morning, and its
behaviour is governed by `morning-briefs/brief-spec.md`, in git.

So the producer side is an edit to a spec file, not new infrastructure. That is most of
what made this "an afternoon" into rather less than one.

## Where the links work, and where they don't

| Surface | Result |
|---|---|
| `morning-brief-*.html` opened in Chrome on this machine | **Works.** One consent dialog per origin, then never again. |
| The Cowork chat that produced the brief | Dead grey text. Markdown sanitizers allowlist `http`/`https`/`mailto`. |
| A phone | Nothing to receive the tap. No Windows handler exists there. |
| GitHub markdown | Stripped, same allowlist. No README badges. |
| Windows Terminal ctrl+click | Works, after one "this isn't a standard link" confirm. |

The desktop column is the one being built for. **The phone half stays the inbox's job** —
the two are not competing designs, they are the two halves of the same brief, and both end
up calling the same verbs.

## The verbs

Three, mirroring the CLI vocabulary, with the harmless one as the default:

```
skysession://show/<id>        select the card in the app. No side effects.
skysession://resume/<id>      confirm, then the existing resume path.
skysession://new?in=<path>    confirm + roots allowlist. Never --trust.
```

`fork` and `restart` are deliberately absent. They are the ones where a bad link costs
something, and neither has a use in a brief.

## The security shape

`ms-msdt` (Follina), `steam://`, `zoommtg://` and a long line of Electron argument-injection
bugs are all one bug: a page navigates to a scheme, Windows appends the attacker's string to
`shell\open\command`, and it executes. Eight rules keep this boring, and none of them are
optional:

1. **Never build a command line from URL text.** Parse the URI in-process, then call the
   same typed entry points `SessionCli` uses. No string concatenation into `cmd`/`powershell`.
2. Register as `"%1"` and re-validate with `Uri.TryCreate`. Reject quotes, newlines, `&`, `|`.
3. **Allowlist the folder.** `in=` must resolve under `~/Code`, exist, and be a repo. Refuse
   UNC, `\?\`, device paths.
4. **`--trust` is never reachable from a link.** Folder trust is Claude Code's actual
   security boundary, and a link is not the operator typing.
5. **No prompt payload that submits.** If `&prompt=` is ever supported it prefills the input
   box and stops there. A link that opens a session *and* sends it a prompt is RCE with extra
   steps.
6. **Confirm in-app.** A small "Resume `<title>`?" / "Start a session in `C:\Code\foo`?"
   dialog. That is what kills the drive-by case, for about twenty lines.
7. `HKCU\Software\Classes\skysession` — per-user, matching the
   `%LOCALAPPDATA%\Programs\SkySessionClaude` install, no admin. Written by `publish.ps1`,
   removed on uninstall.
8. **Route into the running window.** Single-instance handling takes the URL rather than
   starting a second app.

## Build order

1. **`SessionCore/SessionUri.cs`** — parse and validate. Returns a typed request or a
   refusal with a reason; knows nothing about windows or registries. Tests live here: this
   is the whole security boundary, so it is the part that gets the hostile inputs.
2. **`SessionCli link <id>`** — the producer, so there is something emitting links before
   anything consumes them. `link --new <path>` for the other verb.
3. **App route** — `App.xaml.cs` single-instance path takes the URL, the view model shows
   the confirm dialog, then the existing `show` / `resume` / `new` paths run unchanged.
4. **`publish.ps1`** writes the `HKCU` key; the uninstall path removes it.
5. **`brief-spec.md`** — the Needs-attention section emits `skysession://resume/<SessionId>`
   per item. The ids are already in `sessions-unfinished.json`; the brief writes no links at
   all today, so this is additive.

Steps 1–4 are this repo. Step 5 is `morning-briefs`, and takes effect on the next 07:00 run
with no desktop-app edit, because the spec is authoritative for behaviour.

## Verify before building

Open a recent `morning-brief-*.html` in Chrome and click a hand-written
`skysession://x` link. The handler does not exist yet, so the only thing this proves is that
Chrome offers to launch something rather than treating the href as inert — which is exactly
the fact the whole plan rests on.

## Not a dependency

`worktree-force-resume` carries `SessionCli inbox --run` and is still unmerged; main has
advanced past it, so the fast-forward that branch was left expecting is now a real merge.
None of that blocks this. `skysession://` routes into main's existing verbs directly. The
old design's line about the handler being "a second writer to the inbox" was a convenience,
not a requirement, and it is dropped here.
