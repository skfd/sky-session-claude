# gh-release — project specifics for Sky Session Claude

## Versioning: bump only at release time (never skip a version)

The version lives in `src/SessionApp/SessionApp.csproj` (`<Version>`). Between
releases it stays at the **last released** version — dev builds are told apart by
the footer label, which appends the commit hash (`v1.7.0 · 25224cf`).

**Never bump the version in a feature commit.** Bumping is step 1 of the release
itself, so every version number that ever appears in the csproj has exactly one
matching tag and GitHub release. (v1.4.0 and v1.6.0 were skipped this way —
bumped mid-development, released under a later number. Don't repeat that.)

## Release procedure

1. Working tree clean, feature commits already pushed.
2. Decide the next version (minor bump for features, patch for fixes) and set it
   in `src/SessionApp/SessionApp.csproj`.
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

## Release notes style

- Title: `v<version> - Sky Session Claude`.
- `## What's new`: bullets covering everything since the previous **release tag**
  (`git log v<prev>..HEAD --oneline`), each visible UI change with its slice
  image (`width` ≈ 125–480 depending on the crop). Just list the changes — no
  meta-commentary about version history.
- End with the standard `## Downloads` section: both exes, self-contained, the
  SmartScreen "More info -> Run anyway" note.
