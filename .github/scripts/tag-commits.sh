#!/usr/bin/env bash
# Tags, in the local repository, every commit on main's first-parent history that landed after the latest version tag,
# oldest first, so a merge whose own workflow run was cancelled is still tagged (with its tokens) by a later run.
# With no version tag yet, only <head> is considered, so history from before automatic versioning is not tagged.
#
# Usage: tag-commits.sh <head-sha>
# Creates annotated tags locally (the caller pushes them) and prints:
#   tags=<space-separated new tags, possibly empty>
#   release_tag=<tag to build and release, or empty>
#   release=<none|release|prerelease>
# When several commits ask for a release, the last one wins. A [skip version] re-release is acted on only by the run
# that its own commit started. Each line of the commits' outcomes is also written to
# standard error for the job log.
set -euo pipefail

head="$(git rev-parse "$1^{commit}")"
here="$(cd "$(dirname "$0")" && pwd)"
pattern='v[0-9]*.[0-9]*.[0-9]*'

latest_tag() { git tag --list "$pattern" --sort=-v:refname | sed -n '1p'; }

latest="$(latest_tag)"
if [[ -z "$latest" ]]; then
  commits=("$head")
else
  commits=()
  while IFS= read -r commit; do
    commits+=("$commit")
  done < <(git rev-list --first-parent --reverse "$latest..$head")
fi

new_tags=()
release_tag=""
release="none"

for commit in ${commits[@]+"${commits[@]}"}; do
  [[ -z "$commit" ]] && continue
  short="$(git rev-parse --short "$commit")"
  if [[ -n "$(git tag --points-at "$commit" --list "$pattern")" ]]; then
    echo "$short: already tagged" >&2
    continue
  fi

  message="$(git log -1 --format=%B "$commit")"
  if ! result="$("$here/next-version.sh" "$latest" "$message" 2>&1)"; then
    echo "$short: $result" >&2
    echo "::warning::$short: $result"
    continue
  fi

  version="$(sed -n 's/^version=//p' <<<"$result")"
  bump="$(sed -n 's/^bump=//p' <<<"$result")"
  kind="$(sed -n 's/^release=//p' <<<"$result")"

  if [[ "$bump" == "skip" ]]; then
    echo "$short: [skip version] (release: $kind)" >&2
    # A skipped commit is never tagged, so later runs see it again; only the run it started acts on its release.
    if [[ "$kind" != "none" && "$commit" == "$head" ]]; then
      release_tag="$latest"
      release="$kind"
    fi
    continue
  fi

  tag="v$version"
  git tag -a "$tag" -m "Bluestone $tag" "$commit"
  echo "$short: $tag ($bump, release: $kind)" >&2
  new_tags+=("$tag")
  latest="$tag"
  if [[ "$kind" != "none" ]]; then
    release_tag="$tag"
    release="$kind"
  fi
done

echo "tags=${new_tags[*]+"${new_tags[*]}"}"
echo "release_tag=$release_tag"
echo "release=$release"
