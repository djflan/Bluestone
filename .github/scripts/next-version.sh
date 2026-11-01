#!/usr/bin/env bash
# Works out the next version and whether to release, from the latest version tag and a commit message.
#
# Usage: next-version.sh <latest-tag-or-empty> <commit-message>
# Prints three lines:
#   version=<X.Y.Z>   the version to tag, or the latest version when only re-releasing
#   bump=<patch|minor|major|skip>
#   release=<none|release|prerelease>
#
# Tokens (case-insensitive, anywhere in the message):
#   [major] [minor]                       bump that part instead of the patch
#   [skip version]                        do not bump (and do not tag)
#   [release]                             also build and publish a release
#   [pre-release] or [prerelease]         also build and publish a pre-release
# Bump precedence: [skip version] > [major] > [minor] > patch. Release kind: pre-release > release.
# [skip version] with a release token re-releases the latest tag; with no tag yet that is an error.
set -euo pipefail

latest="${1:-}"
message="${2:-}"
lower="$(printf '%s' "$message" | tr '[:upper:]' '[:lower:]')"

has() { [[ "$lower" == *"$1"* ]]; }

release=none
if has '[pre-release]' || has '[prerelease]'; then
  release=prerelease
elif has '[release]'; then
  release=release
fi

bump="patch"
if has '[skip version]'; then
  bump=skip
elif has '[major]'; then
  bump=major
elif has '[minor]'; then
  bump=minor
fi

if [[ -z "$latest" ]]; then
  major=0 minor=0 patch=0
elif [[ "$latest" =~ ^v?([0-9]+)\.([0-9]+)\.([0-9]+)$ ]]; then
  major="${BASH_REMATCH[1]}" minor="${BASH_REMATCH[2]}" patch="${BASH_REMATCH[3]}"
else
  echo "error: '$latest' is not a version tag (vX.Y.Z)" >&2
  exit 2
fi

case "$bump" in
  major) major=$((major + 1)) minor=0 patch=0 ;;
  minor) minor=$((minor + 1)) patch=0 ;;
  patch) patch=$((patch + 1)) ;;
  skip)
    if [[ "$release" != none && -z "$latest" ]]; then
      echo "error: [skip version] with a release token re-releases the latest tag, but there is no tag yet" >&2
      exit 3
    fi
    ;;
esac

echo "version=$major.$minor.$patch"
echo "bump=$bump"
echo "release=$release"
