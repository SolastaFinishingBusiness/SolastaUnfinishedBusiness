#!/usr/bin/env bash
# Prints one release's section of the changelog: the lines under the heading "## <version>" up to the next "## "
# heading or the end of the file, without the blank lines at its start and end. Prints nothing when no heading is
# exactly "## <version>". Usage: release-notes.sh <version> <changelog .md>
set -euo pipefail
tr -d '\r' < "$2" |
  awk -v header="## $1" '
    $0 == header && !found { found = 1; next }
    found && /^## / { exit }
    found { lines[++n] = $0 }
    END {
      first = 1; while (first <= n && lines[first] == "") first++
      last = n; while (last >= first && lines[last] == "") last--
      for (i = first; i <= last; i++) print lines[i]
    }'
