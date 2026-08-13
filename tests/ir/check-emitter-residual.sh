#!/usr/bin/env bash
# Every by-NAME member lookup left in ilemit must be one docs/architecture.md invariant 10 names.
#
# The issue's condition is that ilemit performs no member selection. That is now true, and this keeps it true:
# a new `GetMethod("Something")` is how it would stop being true, quietly, in a file nobody re-reads. The
# allowlist below is the documented residual, not a budget — each entry is a member no lookup can get wrong
# (a delegate's single Invoke, a member of the assembly being built, or the metadata the output format obliges).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

# Names invariant 10 accounts for. Anything else is a member being CHOSEN.
ALLOWED='^(Invoke|GetEnumerator|MoveNext|get_Current|Reset|Dispose|GetTypeFromHandle|Equals|ToString|GetHashCode|CompareTo|GetType|GetValues|Parse|Concat|IndexOf|Add|AddRange|ToArray|GetMethod|CreateDelegate|Combine|Remove|CompareExchange)$'


found=0
while IFS= read -r hit; do
	name="${hit##*(\"}"; name="${name%%\"*}"
	if ! [[ "$name" =~ $ALLOWED ]]; then
		echo "  UNDOCUMENTED  $hit"
		found=1
	fi
done < <(grep -rnoE '\.(GetMethod|GetField)\("[A-Za-z_][A-Za-z0-9_]*"' "$ROOT"/toolchain/ilemit/*.cs || true)

if (( found )); then
	echo "EMITTER RESIDUAL: RED — a by-name member lookup that docs/architecture.md invariant 10 does not account for."
	echo "  Either the member belongs in CIR as a resolved memberRef, or invariant 10 must say why it cannot."
	exit 1
fi
echo "EMITTER RESIDUAL: GREEN — every by-name member lookup in ilemit is one invariant 10 names."
