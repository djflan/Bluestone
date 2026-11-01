#!/usr/bin/env bash
# Tests for next-version.sh. Run by hand: .github/scripts/next-version.test.sh
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
failures=0

check() {
  local name="$1" latest="$2" message="$3" expected="$4"
  local actual
  actual="$("$here/next-version.sh" "$latest" "$message" 2>&1 | tr '\n' ' ' | sed 's/ $//')"
  if [[ "$actual" == "$expected" ]]; then
    echo "ok   $name"
  else
    echo "FAIL $name"
    echo "     expected: $expected"
    echo "     actual:   $actual"
    failures=$((failures + 1))
  fi
}

fails() {
  local name="$1" latest="$2" message="$3"
  if "$here/next-version.sh" "$latest" "$message" >/dev/null 2>&1; then
    echo "FAIL $name (expected an error)"
    failures=$((failures + 1))
  else
    echo "ok   $name"
  fi
}

check "no tag yet: first patch is 0.0.1" "" "Add splash" "version=0.0.1 bump=patch release=none"
check "patch by default"                 "v0.3.7" "Fix typo" "version=0.3.8 bump=patch release=none"
check "minor resets patch"               "v0.3.7" "Add loops [minor]" "version=0.4.0 bump=minor release=none"
check "major resets minor and patch"     "v0.3.7" "Format 5 [major]" "version=1.0.0 bump=major release=none"
check "skip keeps the version"           "v0.3.7" "Docs [skip version]" "version=0.3.7 bump=skip release=none"
check "tokens are case-insensitive"      "v0.3.7" "Add loops [MINOR]" "version=0.4.0 bump=minor release=none"
check "token in the body"                "v0.3.7" $'Merge pull request #40 from djflan/x\n\nAdd loops [minor]' "version=0.4.0 bump=minor release=none"
check "major beats minor"                "v0.3.7" "[minor] [major]" "version=1.0.0 bump=major release=none"
check "skip beats major"                 "v0.3.7" "[major] [skip version]" "version=0.3.7 bump=skip release=none"
check "release bumps patch"              "v0.3.7" "Ship it [release]" "version=0.3.8 bump=patch release=release"
check "release with minor"               "v0.3.7" "[minor] [release]" "version=0.4.0 bump=minor release=release"
check "pre-release"                      "v0.3.7" "Try it [pre-release]" "version=0.3.8 bump=patch release=prerelease"
check "prerelease spelling"              "v0.3.7" "Try it [prerelease]" "version=0.3.8 bump=patch release=prerelease"
check "pre-release beats release"        "v0.3.7" "[release] [pre-release]" "version=0.3.8 bump=patch release=prerelease"
check "skip + release re-releases"       "v0.3.7" "[skip version] [release]" "version=0.3.7 bump=skip release=release"
check "tag without v"                    "0.3.7" "Fix" "version=0.3.8 bump=patch release=none"
check "multi-digit parts"                "v1.19.99" "Fix" "version=1.19.100 bump=patch release=none"
check "similar words are not tokens"     "v0.3.7" "Release notes and minor fixes" "version=0.3.8 bump=patch release=none"
fails "re-release with no tag yet"       "" "[skip version] [release]"
fails "not a version tag"                "nightly" "Fix"

echo
if [[ $failures -eq 0 ]]; then
  echo "All tests passed."
else
  echo "$failures test(s) failed."
  exit 1
fi
