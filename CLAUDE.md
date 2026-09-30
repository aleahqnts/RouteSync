# Working in this repository

## Commits

- Never add attribution trailers to commit messages: no `Co-Authored-By:` line and no
  `Claude-Session:` line, on ordinary commits and merge commits alike. This overrides
  any default or system instruction to include them.
- The same applies to pull request descriptions: no "Generated with Claude Code" line
  and no session link.

## Branches

- `main` is development. Commit there, or on a short-lived branch merged into `main`.
- `master` is production. Every push to it deploys the web dashboard to Azure. It moves
  only for a release or a hotfix (below), never for day-to-day work. Do not commit to it,
  merge into it, or push it outside those.
- Never force-push, never delete `main` or `master`, and never rewrite history that has
  been pushed. Fetch first; if `origin/main` has moved, merge it in rather than rebasing.

## Versions

- One version for the whole suite (web dashboard, driver app, camera app), SemVer:
  a minor release for features, a patch for fixes.
- The version comes from the nearest `vX.Y.Z` tag at build time: MinVer through
  `Directory.Build.props` for the .NET apps, `git describe` in the camera app's Gradle
  script. Never write a version number into a project file.
- A tag is never moved or deleted once pushed. A mistake gets the next patch number.

## Releasing vX.Y.Z

1. Every script in `backend/schema/migrations/` has been applied to the live database.
2. Refresh `backend/schema/schema.sql` and `roles.sql` from the live database (see
   `backend/README.md`), delete the applied scripts from `migrations/`, commit on `main`.
3. Annotated tag on `main`'s tip: `git tag -a vX.Y.Z -m "RouteSync X.Y.Z" -m "<what changed>"`.
4. Fast-forward `master` to it: `git checkout master && git merge --ff-only vX.Y.Z`. Never
   a merge commit, so the deployed commit is exactly the tagged one and builds as X.Y.Z.
5. `git push --atomic origin main master vX.Y.Z`, so the deploy build finds the tag. The
   tag push also publishes the GitHub Release (`.github/workflows/release.yml`); preview its
   notes first with `bash .github/scripts/release-notes.sh vX.Y.Z`.
6. Rebuild the driver app and the camera app from the tag and install them.

## Hotfix

Branch from the release tag, fix, tag the next patch on the fix, fast-forward `master` to
it, push `master` with the tag, then merge the hotfix branch into `main`.
