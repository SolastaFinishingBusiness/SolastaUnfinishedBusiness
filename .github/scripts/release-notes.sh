#!/usr/bin/env bash
# Prints one release's section of the changelog: the lines under the heading "## <version>" up to the next "## "
# heading or the end of the file, without the blank (or whitespace-only) lines at its start and end. Prints nothing
# when no heading is exactly "## <version>". Usage: release-notes.sh <version> <changelog .md>
# awk reads the whole file itself (no pipe into it), so stopping at the next heading can't fail a writer under
# pipefail.
set -euo pipefail
awk -v header="## $1" '
  { sub(/\r$/, "") }
  !found && $0 == header { found = 1; next }
  found && /^## / { done = 1 }
  found && !done { lines[++n] = $0 }
  END {
    first = 1; while (first <= n && lines[first] ~ /^[[:space:]]*$/) first++
    last = n; while (last >= first && lines[last] ~ /^[[:space:]]*$/) last--
    for (i = first; i <= last; i++) print lines[i]
  }' "$2"
