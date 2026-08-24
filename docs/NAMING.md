# Naming sessions

A session's name is the one thing that identifies it on a phone. Today Claude Code derives
most names from the folder — `sky-session-claude-7f`, `-93`, `-9b`, `-87` — and four of
those in one repo tell you nothing. This is the design for Sky taking naming on wherever it
can.

## What is true today (verified against build 2.1.241)

- **Three actors name a session.** Claude Code derives `folder-XX` at launch and records
  `nameSource: "derived"` (or `"collision"`). You name one with `-n/--name`, recorded with
  `nameSource` *omitted*. Sky supplies `--name` on restart and resume.
- **`IsChosen` is the absence of a source, not a value** (`SessionName.cs:37`). Anything Sky
  passes under `--name` therefore reads back as operator-chosen.
- **A live session can be renamed in place, from outside.** Verified end to end against a
  running interactive `cli` session. Newline-delimited JSON over the named pipe in
  `messagingSocketPath`; the first line must be `{"type":"auth","token":"<peerToken>"}` read
  from the sibling `<pid>.<hash>.key`, then `{"type":"control","action":"rename","name":"…"}`.
  An unauthenticated connection has its lines dropped and is closed.
- **Every live session publishes that pipe**, including the desktop app and the SDK — all
  nine non-terminal sessions in this registry have both a `messagingSocketPath` and a key.
  So rename reaches sessions restart cannot.
- **A pipe rename is indistinguishable from one you typed.** It writes `nameSource` absent,
  exactly like `--name`. The `"remote"` source in the binary belongs to the SDK
  control-request path, not this one. There is no field that tells Sky which names are its
  own, which is why provenance has to be stored.
- **`aiTitle` is generated once and never updated.** In `comentality.com` it appears 44
  times, always the same string. A long session stays named after its first ten minutes.
- **Nothing is upgrading derived names in practice.** The schema allows `nameSource: "auto"`
  and the binary has a path that writes it, but no session in this registry carries one:
  eight sit on derived names, three with a perfectly good unused `aiTitle` already in file.

### Two bugs this design has to fix

1. **Naming writes into the transcript.** `RestartPolicy.cs:110` claims otherwise — *"--name
   is written only to the live registry, never to the transcript"*. That is false. A named
   launch appends `{"type":"custom-title"}` to the `.jsonl`, and `SessionFileParser.cs:132`
   resolves `Name = custom ?? name`, so **a custom title outranks the AI one**. This applies
   to pipe renames too: the smoke test renamed a session that had no transcript at all, and
   the rename *created* the file just to hold the title line.
2. **The loop is self-reinforcing.** Next restart reads that slug back as `Title`,
   `SessionName.For` returns it unchanged, and Sky writes it again. A placeholder becomes
   permanent by being used once.

## Decisions

**Reach.** Sky names at every moment it can: launch verbs (`new`, `resume`, `restart`),
inbox-queued actions, and live sessions over the pipe — **including sessions it cannot
restart**. Restart is refused because work could be lost; a rename drops nothing, so the
refusal does not carry over. That alone fixes `code-20`, `cowork-7a`, `cowork-57` and
`cowork-48`.

**What a name refers to.** The subject first, then the folder: `Subject — folder`. The
subject is what the session was *about*, which is not always where it ran — `93e5d264` sat
in `cowork` while the work was vagabond-map renderers and routers.

**Which folder.** The repo, not the worktree. Three sessions under
`sky-session-claude\.claude\worktrees\force-resume` all read `— sky-session-claude`; their
subjects are what tell them apart.

**Drift.** The name follows the largest thing the session did, not the newest. `d1ffa628` is
titled *"Start Chrome"* — a first step — and ended rewriting `~/.claude/settings.json`. A
trivial detour does not earn a rename. This needs judgement, so it is the model's call, not
a rule.

**House style.** Sentence case, plain words — *"Basemap treatments in Chrome"*. Matches what
the model already writes, so Sky's names and Claude's are indistinguishable, which is the
point: both are describing the same thing.

**Collisions override provenance.** A name you chose is normally untouchable. The one
exception is when two live sessions carry the same one: three of yours all read *"vagabond
maps"*, and identical rows identify nothing. There the subject wins and replaces it. *This
is the only place Sky overwrites something you typed — the sharpest edge in this design.*

**Content-free sessions** get `folder-<id2>`. *Interpretation, flag if wrong:* this applies
only where there is no chosen name to lose. Replacing your *"vagabond maps"* with
`vagabond-map-69` would be strictly worse, so a content-free session already carrying a name
you typed keeps it.

**Breaking the self-reinforcing loop.** Since a slug now reaches the transcript, Sky
recognises its own slug shape on read and refuses to treat it as a title. That keeps the
floor without letting a placeholder calcify. The shape check is only for history written
before the sidecar existed — it can misfire on a name you genuinely typed that happens to
look like `folder-XX`, so provenance stays the primary mechanism and this is the fallback.

**Fork naming** *(my recommendation, not confirmed)*. `fork: ` plus the prompt it branched
at — `fork: add retry logic`. Sky knows it from `--at-prompt n`, and it says what the fork is
*for*, where the parent's title makes every fork look identical.

**What runs the check.** Both, over one policy in `SessionCore`. The app already polls, so
it does the noticing and renames in the background — that is what makes "track the
conversation" true rather than aspirational. `SessionCli rename` exists alongside it, because
the CLAUDE.md self-rename needs something to call and a `--self` form needs the same
current-session detection `IsSelf` already does for `restart`. Doing it only at restart time
would be cheaper and would quietly mean a session keeps a wrong name until something
unrelated restarts it.

**Provenance.** Because nothing in the registry distinguishes Sky's names from yours, Sky
records its own in a sidecar alongside `DispositionStore`. Without it, none of the above is
reachable.

### Where names come from, in order

1. **The session renames itself.** A CLAUDE.md line asks it to, **when the subject really
   changes** — not every task, not every turn. Free, no extra call, and the only source that
   knows what the conversation is doing now.
2. **`aiTitle` from the transcript**, when there is one and nothing better exists. Instant,
   offline, deterministic — and it alone fixes most of today's ambiguity, since the titles
   already exist and simply go unused.
3. **`claude -p --model haiku`**, only when self-naming did not happen — an old session, one
   started before the instruction existed. Rare and bounded by construction.
4. **`folder-<id2>`** as the floor.

### The CLAUDE.md line

Drafted here, **installed only once `SessionCli rename` exists** — the line is useless until
it has something to call:

> When this session's subject genuinely changes, rename it:
> `SessionCli rename --self '<Subject — repo>'`

Around 120 characters, which is the point: it has to earn its place in every project's
CLAUDE.md. It says *genuinely changes*, not *after each task*, to match the drift decision.

## Worked examples

| Session | Today | Becomes |
|---|---|---|
| `93e5d264` cowork, OSM renderers | `Renderers and routers approach ove…` | `Renderers and routers — cowork` |
| `360a1c31` vagabond-map, basemaps | `vagabond maps` | `Basemap treatments in Chrome — vagabond-map` |
| `7fa99fa7` vagabond-map, ends on a push | `vagabond maps` | the push is the *newest* thing, not the largest — its recap records only the ending, so this is precisely the case that needs the model rather than a rule |
| `697155ed` vagabond-map, no content | `vagabond maps` | `vagabond maps` — chosen, nothing to improve on |
| `c6811c81` force-resume worktree | `sky-session-claude-87` | `Protocol implementation brainstorm — sky-session-claude` |
| `d1ffa628` cowork, ended in settings.json | `Start Chrome` | the largest thing it did, not the first step |
| `code-20` desktop app, `~/Code` | `code-20` | renamed over the pipe, though it cannot be restarted |

## The `claude -p` path

Measured, not assumed:

| | |
|---|---|
| Wall time, as-is | 8.9s — most of it MCP servers booting |
| With MCP stripped | 5.3s |
| Cost per name | $0.020 |
| Side effect | a 16–22KB session file that shows up in `SessionCli list` |

Rules that follow:

- **Prompt on stdin.** `--disallowedTools` and friends are variadic and swallow a trailing
  prompt argument.
- **MCP off** via `--strict-mcp-config` with an empty server set. Naming needs no Figma
  connector, and it is 3.6s of the 8.9.
- **`--output-format json`, always.** It returns `session_id`, which is what makes cleanup
  safe: Sky deletes exactly the transcript its own call created, never one it inferred.
- **Run it in a scratch cwd**, so a call interrupted before cleanup leaks into an obvious
  junk folder rather than into `~` or a real repo. Sessions Sky launches *for you* are
  first-class and carry no marking — this applies only to internal naming calls.
- **Strip the answer.** Haiku returns the name wrapped in backticks. Everything goes through
  `SessionName.Tidy` regardless.
- **It blocks.** The call sits in the restart path and the caller waits. Tolerable only
  because step 3 is rare — a sweep where nothing had self-named would add ~5s per session.

## Open

- Cleanup deletes files under `~/.claude/projects` — a new authority for a tool that has so
  far only read them, bounded to ids `claude -p` reported back.
- One-off repair of the transcripts already carrying a Sky slug as `custom-title`:
  `xrm-librarian`, `xrm-plugin-step-codegen`, `ontario-address-changes`, and
  `sky-session-claude-93`, whose file exists only because the smoke test created it.
- ~~`restart --stale` crashes with a duplicate-key `ArgumentException`.~~ Not a live bug:
  `6b7d877` already fixed it by collapsing two transcripts of one id in `SelectFiles`. The
  installed binary predated that commit. Fixed by publishing.
