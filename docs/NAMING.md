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
- **A live session can be renamed in place, from outside.** The messaging pipe each
  session publishes (`~/.claude/sessions/<pid>.json` → `messagingSocketPath`, authenticated
  by the sibling `<pid>.<hash>.key`) accepts `{type:"control", action:"rename", name:"…"}`.
  Confirmed empirically: `comentality.com re` (entrypoint `cli`) was renamed 75 minutes
  after start, `skfd` at 66 minutes.
- **`aiTitle` is generated once and never updated.** In `comentality.com` it appears 44
  times, always the same string. A long session stays named after its first ten minutes.
- **Claude Code does not upgrade derived names on its own.** No session in the registry
  carries `nameSource: "auto"`; eight sit on raw derived names, three with a perfectly good
  unused `aiTitle` in their file.

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
4. **`folder-<id2>`** as the floor, where nothing else can be known.

**Fork naming.** `fork: ` plus the prompt it branched at — `fork: add retry logic`. Sky
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
- The one-off repair of the three transcripts already carrying a Sky slug as `custom-title`.
- `restart --stale` still crashes with a duplicate-key `ArgumentException`. Unrelated, open.
