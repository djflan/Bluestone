#!/usr/bin/env bash
# Tests for tag-commits.sh in a scratch repository. Run by hand: .github/scripts/tag-commits.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(mktemp -d)"
trap 'rm -rf "$repo"' EXIT
cd "$repo" || exit 1
git init -q -b main
git config user.name test
git config user.email test@example.com
git commit -q --allow-empty -m "History from before automatic versioning"
git commit -q --allow-empty -m "More old history"

failures=0
merge() { git commit -q --allow-empty -m "$1"; }
run() { "$here/tag-commits.sh" "$(git rev-parse HEAD)" 2>/dev/null | tr '\n' ' ' | sed 's/ $//'; }
check() {
  local name="$1" expected="$2" actual
  actual="$(run)"
  if [[ "$actual" == "$expected" ]]; then
    echo "ok   $name"
  else
    echo "FAIL $name"
    echo "     expected: $expected"
    echo "     actual:   $actual"
    failures=$((failures + 1))
  fi
}
tags() { git tag --list 'v*' --sort=v:refname | tr '\n' ' ' | sed 's/ $//'; }

merge "Add splash"
check "first run tags only the head, not old history" "tags=v0.0.1 release_tag= release=none"
[[ "$(tags)" == "v0.0.1" ]] || { echo "FAIL old history was tagged: $(tags)"; failures=$((failures + 1)); }

check "re-run on a tagged head does nothing" "tags= release_tag= release=none"

merge "Fix [minor]"
check "minor" "tags=v0.1.0 release_tag= release=none"

# Two merges whose runs were cancelled, then a third: all three are tagged in order, with their tokens.
merge "Merge A [release]"
merge "Merge B [skip version]"
merge "Merge C"
check "cancelled runs are caught up, last release wins" "tags=v0.1.1 v0.1.2 release_tag=v0.1.1 release=release"
[[ "$(git tag --points-at HEAD~1)" == "" ]] || { echo "FAIL skipped commit was tagged"; failures=$((failures + 1)); }

merge "Merge D [pre-release]"
merge "Merge E [release]"
check "the last release request wins" "tags=v0.1.3 v0.1.4 release_tag=v0.1.4 release=release"

merge "Docs [skip version] [pre-release]"
check "re-release the latest tag" "tags= release_tag=v0.1.4 release=prerelease"

merge "Fix F"
check "an earlier re-release is not repeated by later runs" "tags=v0.1.5 release_tag= release=none"

merge "Big change [major] [prerelease]"
check "major with pre-release" "tags=v1.0.0 release_tag=v1.0.0 release=prerelease"

echo
echo "tags: $(tags)"
if [[ $failures -eq 0 ]]; then
  echo "All tests passed."
else
  echo "$failures test(s) failed."
  exit 1
fi
