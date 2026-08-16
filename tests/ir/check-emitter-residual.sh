#!/usr/bin/env bash
# Every member lookup left in ilemit must carry, at its site, the reason invariant 10 allows it.
#
# The issue's condition is that ilemit performs no member selection. A prose claim of that shape decays the
# moment someone adds a lookup to a file nobody re-reads, so it is checked here — and checked for BEHAVIOUR,
# not spelling. Three shapes say "find me the member called X":
#
#   GetMethod("Combine")                        the name written
#   GetMethod(add ? "Combine" : "Remove", …)    the name computed
#   GetMethods(…).Single(m => m.Name == "X")    the candidate set enumerated and filtered by name
#
# An earlier version of this check saw only the first, and reported green while the other two were live in the
# same file. All three are flagged now, and a site is allowed only if it says why on the spot: put
# `#370-residual: <reason>` on the line or just above it. That keeps the justification where the code is rather
# than in a list that drifts from it.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
MARK='#370-residual'

# ALL THREE shapes, always. The first version of this check saw only literals and reported green with a computed
# name live in the file it had just cleared; the second traded the literal check away for the computed one and
# reported green with `GetMethod("GetEnumerator")` fallbacks live in another. Narrowing this set has now caused
# the exact failure it was narrowed to fix, twice. It does not get narrowed again.
lookups() {
	grep -rnE '\.(GetMethod|GetField)\("' "$ROOT"/toolchain/ilemit/*.cs | grep -vE ':[0-9]+: *(//|\*)'
	# Constructors count too. The test is not whether a NAME was used to find the member — it is whether an
	# EXTERNAL member reaches a CIL operand, and `newobj` needs a constructor token exactly as `call` needs a
	# method one. Leaving `GetConstructor` out was reasoning about spelling again.
	grep -rnE '\.(GetMethod|GetField|GetConstructor)\(([a-z_][A-Za-z0-9_]*[,)]|[A-Za-z0-9_.()"]+ *\?|new\[|Type\.EmptyTypes)' "$ROOT"/toolchain/ilemit/*.cs \
		| grep -vE ':[0-9]+: *(//|\*)'
	grep -rnE '\.Name *== *"' "$ROOT"/toolchain/ilemit/*.cs | grep -vE ':[0-9]+: *(//|\*)'
}

found=0
while IFS= read -r hit; do
	[[ -n "$hit" ]] || continue
	file="${hit%%:*}"; rest="${hit#*:}"; line="${rest%%:*}"
	# the site itself, or the two lines above it, must carry the marker
	if ! sed -n "$((line > 2 ? line - 2 : 1)),${line}p" "$file" | grep -q -- "$MARK"; then
		echo "  UNJUSTIFIED  ${hit:0:150}"
		found=1
	fi
done < <(lookups)

if (( found )); then
	echo "EMITTER RESIDUAL: RED — a member lookup with no stated reason."
	echo "  Either the member belongs in CIR as a resolved memberRef, or mark the site '$MARK: <reason>'"
	echo "  and make sure docs/architecture.md invariant 10 covers that reason."
	exit 1
fi
echo "EMITTER RESIDUAL: GREEN — every member lookup in ilemit states why invariant 10 allows it."
