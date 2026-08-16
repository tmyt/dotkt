#!/usr/bin/env bash
# Every member lookup left in ilemit must carry, at its site, the reason invariant 10 allows it.
#
# The issue's condition is that ilemit performs no member selection. A prose claim of that shape decays the
# moment someone adds a lookup to a file nobody re-reads, so it is checked here. Every direct singular lookup is
# covered regardless of how its argument expression is spelled; enumeration-and-filter selection is covered too:
#
#   GetMethod("Combine")                        the name written
#   GetMethod(add ? "Combine" : "Remove", …)    the name computed
#   GetMethods(…).Single(m => m.Name == "X")    the candidate set enumerated and filtered by name
#
# An earlier version enumerated selected argument spellings and reported green while
# `GetField(e.GetProperty("name").GetString(), ...)` remained live. All forms are flagged now, and a site is allowed
# only if it says why on the spot: put
# `#370-residual: <reason>` on the line or just above it. That keeps the justification where the code is rather
# than in a list that drifts from it.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
MARK='#370-residual'

# Every singular lookup, independent of argument syntax, plus enumeration filtered by Name. Do not return to an
# argument-shape allow-list: literal, variable, conditional, and JsonElement-derived arguments have all escaped one.
lookups() {
	grep -rnE '\.(GetMethods?|GetFields?|GetConstructors?)\(' "$ROOT"/toolchain/ilemit/*.cs \
		| grep -vE ':[0-9]+: *(//|\*)'
	grep -rnE '\.Name *(==|!=) *' "$ROOT"/toolchain/ilemit/*.cs | grep -vE ':[0-9]+: *(//|\*)'
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
# The generic-frame clone this used to pin belonged to the emitter-authored Unit delegate adapter, which is gone:
# bir2cir now authors that adapter as an ordinary CIR class whose parameters ARE the delegate's own, so it declares
# no constraints to clone and no anti-constraint to preserve. Nothing in ilemit rewrites a generic frame any more,
# which is what the pin was guarding; the check below is the general one.
if grep -qE 'DefineGenericParameters' "$ROOT/toolchain/ilemit/Emitter.Delegates.cs" \
	&& ! grep -qF 'PersistedAssemblyBuilder' "$ROOT/toolchain/ilemit/Emitter.Delegates.cs"; then
	echo "EMITTER RESIDUAL: RED — a generic frame is minted in Emitter.Delegates.cs with no encoding-workaround rationale."
	exit 1
fi
echo "EMITTER RESIDUAL: GREEN — every member lookup in ilemit states why invariant 10 allows it."
