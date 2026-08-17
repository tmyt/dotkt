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
# A generic frame MINTED in the delegate emitter is the shape the retired Unit adapter had: a helper the emitter
# invents, over parameters it chooses, whose constraints it must then reconstruct. bir2cir authors that adapter as
# an ordinary CIR class now (including the `allows ref struct` anti-constraint its parameters owe — pinned by
# tests/ir/lowering/unit-delegate-adapter.assert and driven by the interop battery's Span fixture), so what is left
# here may only be an encoding workaround, and each one says so on the spot. Same rule and same shape as the
# member-lookup check above: the marker sits at the site, not in a list that drifts from it.
FRAME_MARK='#400-residual'
frames=0
while IFS= read -r hit; do
	[[ -n "$hit" ]] || continue
	frames=1
	line="${hit%%:*}"
	if ! sed -n "$((line > 2 ? line - 2 : 1)),${line}p" "$ROOT/toolchain/ilemit/Emitter.Delegates.cs" \
		| grep -q -- "$FRAME_MARK"; then
		echo "EMITTER RESIDUAL: RED — a generic frame is minted at Emitter.Delegates.cs:$line with no stated reason."
		echo "  A frame the emitter invents is a declaration CIR did not state. Either bir2cir authors it, or mark"
		echo "  the site '$FRAME_MARK: <encoding reason>'."
		exit 1
	fi
done < <(grep -nE '\.DefineGenericParameters\(' "$ROOT/toolchain/ilemit/Emitter.Delegates.cs" | cut -d: -f1)
if (( ! frames )); then
	echo "EMITTER RESIDUAL: RED — the minted-frame check matched nothing; its grep no longer describes the code."
	exit 1
fi
echo "EMITTER RESIDUAL: GREEN — every member lookup in ilemit states why invariant 10 allows it."
