# Naming sessions

A session's name is the one thing that identifies it on a phone. Today Claude Code
derives most names from the folder — `sky-session-claude-7f`, `sky-session-claude-93`,
`sky-session-claude-9b` — and four of those in one repo tell you nothing. This is the
design for Sky taking naming on wherever it can.

## What is true today (verified against build 2.1.241)

- **Three actors name a session.** Claude Code derives `folder-XX` at launch and records
  `nameSource: "derived"` (or `"collision"`). You name one with `-n/--name`, recorded with
  `nameSource` *omitted*. Sky supplies `--name` on restart and resume.
- **`IsChosen` is the absence of a source, not a value** (`SessionName.cs:37`). Anything
  Sky passes under `--name` therefore reads back as operator-chosen.
- **A live session can be renamed in place, from outside.** Verified end to end against a
  running interactive `cli` session. The protocol is newline-delimited JSON over the named
  pipe in `messagingSocketPath`; the first line must be
  `{"type":"auth","token":"<peerToken>"}`, read from the sibling `<pid>.<hash>.key`, and a
  rename is then `{"type":"control","action":"rename","name":"…"}`. An unauthenticated
  connection has its lines dropped and is closed.
- **`aiTitle` is generated once and never updated.** In `comentality.com` it appears 44
  times, always the same string. A long session stays named after its first ten minutes.
- **Nothing is upgrading derived names in practice.** The schema allows
  `nameSource: "auto"` and the binary has a path that writes it, but no session in this
  registry carries one: eight sit on raw derived names, three with a perfectly good unused
  `aiTitle` already in their file.

- **A pipe rename is indistinguishable from one you typed.** It writes `nameSource`
  *absent*, exactly like `--name`. The `"remote"` source in the binary belongs to the SDK
  control-request path, not this one. So the provenance sidecar below is load-bearing: there
  is no field that tells Sky which names are its own.

### Two bugs this design has to fix

1. **`--name` writes into the transcript.** `RestartPolicy.cs:110` claims it does not —
   *"--name is written only to the live registry, never to the transcript"*. That comment is
   false. A named launch appends `{"type":"custom-title"}` to the `.jsonl`, and
   `SessionFileParser.cs:132` resolves `Name = custom ?? name`, so **a custom title outranks
   the AI one**. Five sessions restarted on 2026-08-23 now carry Sky's `--name` as their
   transcript title; three of those are folder slugs (`xrm-librarian-1c`,
   `xrm-plugin-step-codegen-a2`, `ontario-address-changes-b9`) now permanently outranking
   the sessions' own titles.
2. **The loop is self-reinforcing.** Next restart reads that slug back as `Title`,
   `SessionName.For` returns it unchanged, and Sky writes it again. A placeholder becomes
   permanent by being used once.

Both apply to live renames too, not just `--name`: the smoke test renamed a session that had
no transcript at all, and the rename *created* the file just to hold a `custom-title` line.
Every naming path Sky has writes into the conversation's own record.

## Decisions

**Reach.** Sky names at every moment it can: the launch verbs (`new`, `resume`, `restart`),
inbox-queued actions, and live sessions via a pipe rename. Plus a short CLAUDE.md line
telling a session to keep its own name current.

**Freshness.** Names track the conversation rather than freezing. A name Sky set is Sky's to
replace; a name you set is never touched.

**Provenance.** Because `IsChosen` cannot distinguish Sky's names from yours, Sky records
its own in a sidecar alongside `DispositionStore`. Without it, every fix above is
unreachable.

**Where names come from, in order:**

1. **The session renames itself.** A CLAUDE.md line asks the running session to keep its
   name current through `SessionCli rename`. Free, no extra model call, and the only source
   that knows what the conversation is doing right now.
2. **`aiTitle` from the transcript**, when there is one and nothing better exists. Instant,
   offline, deterministic — and it alone fixes most of today's ambiguity, since the titles
   already exist and simply go unused.
3. **`claude -p --model haiku`**, only when self-naming did not happen — an old session, one
   started before the instruction existed. Rare and bounded by construction.
4. **`folder-<id2>`** as the floor, where nothing else can be known. *Not yet confirmed:*
   keeping a floor at all was chosen before we knew a slug overwrites the transcript title.
   The alternative is to pass no name when there is nothing real to say.

**What runs the check** *(open — never decided)*. Live renames need something noticing that a
session earned a title. The app already polls and could do it in the background; a
`SessionCli rename` verb is needed regardless, for the CLAUDE.md self-rename to call. Doing
it only at restart time would be cheapest and would quietly contradict "track the
conversation continuously".

**Fork naming** *(my recommendation, not yet confirmed)*. `fork: ` plus the prompt it branched at — `fork: add retry logic`. Sky
already knows it from `--at-prompt n`, and it says what the fork is *for*, where the parent's
title would make every fork of one session look identical.

## The `claude -p` path

Measured, not assumed:

| | |
|---|---|
| Wall time, as-is | 8.9s — most of it MCP servers booting |
| With `--strict-mcp-config --mcp-config '{"mcpServers":{}}'` | 5.3s |
| Cost per name | $0.020 |
| Side effect | a 16–22KB session file that shows up in `SessionCli list` |

Rules that follow:

- **Prompt goes on stdin.** `--disallowedTools` and friends are variadic and swallow a
  trailing prompt argument (`Error: Input must be provided either through stdin or as a
  prompt argument`).
- **MCP off.** Nothing about naming needs a Figma connector, and it is 3.6s of the 8.9.
- **`--output-format json`, always.** It returns `session_id`, which is what makes cleanup
  safe: Sky deletes exactly the transcript its own call created, never a file it inferred.
- **Strip the answer.** Haiku returns "`vault-download-retries`" with backticks. Names go
  through `SessionName.Tidy` regardless.
- **It blocks.** The naming call sits in the restart path and the caller waits. This is
  tolerable only because step 3 is rare — a sweep where nothing had self-named would add
  ~5s per session.

## Open

- Cleanup deletes files under `~/.claude/projects`. That is a new authority for a tool that
  has so far only ever read them; it is bounded to session ids `claude -p` reported back.
- The one-off repair of the transcripts already carrying a Sky slug as `custom-title`:
  `xrm-librarian`, `xrm-plugin-step-codegen`, `ontario-address-changes`, and
  `sky-session-claude-93`, whose file exists only because the smoke test created it.
- `restart --stale` still crashes with a duplicate-key `ArgumentException`. Unrelated, open.
