# gh-release — project specifics for Sky Session Claude

## Versioning: dev builds carry `-dev`, releases drop it

One `<Version>` for the whole product lives in `src/Directory.Build.props` — the
app and the CLI ship in the same GitHub release and must never drift apart.

Between releases it is the **next patch with a `-dev` suffix**: the moment v1.7.0
ships, main moves to `1.7.1-dev`. The footer renders that straight from
InformationalVersion, so a dev build reads `v1.7.1-dev · 25224cf` and can never be
mistaken for the release it followed.

Patch, not minor, is deliberate: `1.7.1-dev` sorts below both `1.7.1` and `1.8.0`,
so whichever number the next release takes, the dev build never claimed a higher
one. `1.7.1-dev` never getting a `1.7.1` release is fine — the suffix marks it as
not-a-version.

**Never bump in a feature commit.** The two moments a version changes are step 2
of a release (drop the suffix, or bump further) and step 8 (open the next `-dev`).
That keeps the invariant: every **stable** version has exactly one matching tag and
GitHub release. (v1.4.0 and v1.6.0 were skipped by bumping mid-development back
when dev builds carried a bare release number. The suffix is what prevents that.)

## Release procedure

1. Working tree clean, feature commits already pushed.
2. Decide the next version and set it in `src/Directory.Build.props`: drop the
   `-dev` suffix for a patch release (`1.7.1-dev` -> `1.7.1`), or bump to the
   feature number (`1.7.1-dev` -> `1.8.0`).
3. Capture the changelog screenshot slice(s) for any visible UI change into
   `docs/changelog/v<version>-<slug>.png` — tiny crops of just the changed
   element, not full-window shots. Reference them in the notes via
   `https://raw.githubusercontent.com/skfd/sky-session-claude/main/docs/changelog/...`
   (commit the images to main before publishing, or the links 404).
4. Commit the bump + slices: "Release v<version>".
5. `./publish.ps1` — builds `dist/SkySessionClaude.exe` + `dist/SessionCli.exe`
   and refreshes the stable Start-menu install.
6. Push main, tag `v<version>`, push the tag.
7. `gh release create v<version> dist/SkySessionClaude.exe dist/SessionCli.exe`
   with the notes.
8. Open the next development version: set `src/Directory.Build.props` to the next
   patch plus `-dev` (released 1.7.1 -> `1.7.2-dev`; released 1.8.0 -> `1.8.1-dev`),
   commit as "Open v<next>-dev", push. Do this immediately — main should never sit
   at a bare released version.

## Rewriting commits before a release

Tidying the unpushed stack is conflict-free here because no feature commit touches
the version. Two things general git sense won't tell you:

- `Open v<n>-dev` is the first commit above a release tag, so it marks where
  rewriting becomes safe — and it has to stay first and alone. Squash it into a
  later commit and everything between the tag and the bump reads as the bare
  released version again, the ambiguity the suffix exists to remove.
- The notes are generated from `git log v<prev>..HEAD --oneline`, one bullet per
  commit. Collapsing the stack into a single "Release" commit destroys the source
  they are written from.

## Release notes style

- Title: `v<version> - Sky Session Claude`.
- `## What's new`: bullets covering everything since the previous **release tag**
  (`git log v<prev>..HEAD --oneline`), each visible UI change with its slice
  image (`width` ≈ 125–480 depending on the crop). Just list the changes — no
  meta-commentary about version history. `Open v<n>-dev` shows up in that log range;
  it is bookkeeping, not a change — leave it out.
- End with the standard `## Downloads` section: both exes, self-contained, the
  SmartScreen "More info -> Run anyway" note.
