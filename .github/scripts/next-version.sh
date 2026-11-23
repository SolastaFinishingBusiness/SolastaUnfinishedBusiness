#!/usr/bin/env bash
# Prints the version develop should carry after a release, or nothing when develop needs no change.
# Usage: next-version.sh <released version> <develop's version>; both must be MAJOR.MINOR.PATCH in plain decimal.
# - develop carries the released version: the next minor version (1.19.0 or 1.19.1 released -> 1.20.0), so develop's
#   test builds ("<version>-dev.<commit>") sort above the release and below their own release;
# - develop is already ahead (a patch released from an older release tag while develop carries the next minor):
#   nothing;
# - develop is behind the release: nothing, with a warning on stderr (develop should never be behind a release).
# Versions are compared number by number, so 1.10.0 is above 1.9.0. Messages go to stderr; stdout is only the version.
# --test runs the cases below and exits non-zero on a wrong answer.
set -euo pipefail

parse() {
  if [[ ! "$1" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
    echo "::error::'$1' is not a MAJOR.MINOR.PATCH version" >&2
    return 1
  fi
  echo "${BASH_REMATCH[1]} ${BASH_REMATCH[2]} ${BASH_REMATCH[3]}"
}

next_version() {
  local released develop
  released=$(parse "$1") || return 1
  develop=$(parse "$2") || return 1
  read -r r1 r2 r3 <<< "$released"
  read -r d1 d2 d3 <<< "$develop"
  local order=0 i
  local r=("$r1" "$r2" "$r3") d=("$d1" "$d2" "$d3")
  for i in 0 1 2; do
    if (( d[i] > r[i] )); then order=1; break; fi
    if (( d[i] < r[i] )); then order=-1; break; fi
  done
  if (( order == 0 )); then
    echo "$r1.$((r2 + 1)).0"
  elif (( order < 0 )); then
    echo "::warning::develop carries $2, below the released $1; left as it is" >&2
  fi
}

if [ "${1:-}" = "--test" ]; then
  failed=0
  check() {
    local got
    got=$(next_version "$1" "$2" 2> /dev/null) || got="error"
    if [ "$got" != "$3" ]; then echo "next-version $1 $2: expected '$3', got '$got'" >&2; failed=1; fi
  }
  check 1.19.0 1.19.0 1.20.0    # the usual case: develop was released as it is
  check 1.19.1 1.19.1 1.20.0    # a patch released from develop
  check 1.9.0 1.9.0 1.10.0      # numbers, not text
  check 2.0.0 2.0.0 2.1.0       # a major release
  check 1.19.1 1.20.0 ""        # a patch from an older tag: develop already ahead
  check 1.9.0 1.10.0 ""         # ahead by number, behind as text
  check 1.20.0 1.19.0 ""        # develop behind (warning only)
  check 1.19 1.19.0 error       # not three numbers
  check 1.19.0 1.19.0-dev.1 error
  check 01.19.0 1.19.0 error
  exit "$failed"
fi

next_version "$1" "$2"
