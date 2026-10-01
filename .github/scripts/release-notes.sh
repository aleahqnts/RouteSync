#!/usr/bin/env bash
# Writes the GitHub Release notes for one release tag, as Markdown, to standard output.
#
#   .github/scripts/release-notes.sh v1.2.0
#
# The notes are built from the repository itself, so they can be previewed locally before a
# release and read the same on GitHub:
#
#   - what the release is, from the tag's own message;
#   - the database scripts it brought, which must be applied before its code runs;
#   - the version code each phone app carries, which is how a phone orders its installs,
#     for releases built since the apps read their version from the tag;
#   - every change since the release before it, from the commit subjects.
#
# The release before it is the nearest earlier vX.Y.Z tag in the tag's history.

set -euo pipefail

tag="$1"
version="${tag#v}"
IFS=. read -r major minor patch <<<"$version"

previous="$(git describe --tags --abbrev=0 --match 'v[0-9]*' "$tag^" 2>/dev/null || true)"
range="${previous:+$previous..}$tag"

echo "$(git tag -l --format='%(contents:body)' "$tag" | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}')"
echo

echo "## Database"
echo
migrations="$(git log --diff-filter=A --name-only --format= "$range" -- backend/schema/migrations \
  | grep '\.sql$' | grep -v -e '-test\.sql$' -e '-rollback\.sql$' | sort -u || true)"
if [ -n "$migrations" ]; then
  echo "Apply these to the live database before this release's code runs, in date order:"
  echo
  echo "$migrations" | sed 's|^backend/schema/migrations/|- `|; s|$|`|'
else
  echo "No database changes."
fi
echo

# Releases from before the apps read their version from the tag carry the fixed numbers
# they were built with, so nothing is said about a code they did not have.
if git show "$tag:RouteSyncMobile/FleetWiseMobile.csproj" 2>/dev/null | grep -q SetVersionFromTag; then
  echo "## Phone apps"
  echo
  echo "Install the driver app and the camera app from the APKs attached below. Both are"
  echo "version $major.$minor.$patch and install over an earlier version without uninstalling it."
  echo
fi

count="$(git rev-list --no-merges --count "$range")"
echo "## Changes"
echo
echo "<details><summary>${count} change$([ "$count" = 1 ] || echo s)${previous:+ since $previous}</summary>"
echo
git log --no-merges --reverse --format='- %s' "$range"
echo
echo "</details>"
