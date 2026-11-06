#!/usr/bin/env bash
# Every member lookup left in ilemit must carry, at its site, the reason the external-member rule in
# docs/architecture.md allows it.
#
# The enforced condition is that ilemit performs no member selection. A prose claim of that shape decays the
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
# `member-lookup-residual: <reason>` on the line or just above it. That keeps the justification where the code is rather
# than in a list that drifts from it.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
MARK='member-lookup-residual'
sources=("$ROOT"/toolchain/ilemit/*.cs)
# Missing/unreadable input is not evidence that the emitter contains zero residuals.
for file in "${sources[@]}" "$ROOT/toolchain/ilemit/Emitter.Delegates.cs"; do
	if [[ ! -f "$file" || ! -r "$file" ]]; then
		echo "EMITTER RESIDUAL: RED — cannot read source: $file"
		exit 1
	fi
done

# Every singular lookup, independent of argument syntax, plus enumeration filtered by Name. Do not return to an
# argument-shape allow-list: literal, variable, conditional, and JsonElement-derived arguments have all escaped one.
scan_rc=0
hits="$(grep -nHE '\.(GetMethods?|GetFields?|GetConstructors?)\(|\.Name *(==|!=) *' "${sources[@]}")" || scan_rc=$?
if (( scan_rc > 1 )); then
	echo "EMITTER RESIDUAL: RED — member-lookup source scan failed."
	exit 1
fi

found=0
while IFS= read -r hit; do
	[[ -n "$hit" ]] || continue
	file="${hit%%:*}"; rest="${hit#*:}"; line="${rest%%:*}"
	code="${rest#*:}"
	[[ "$code" =~ ^[[:space:]]*(//|\*) ]] && continue
	# the site itself, or the two lines above it, must carry the marker
	if ! sed -n "$((line > 2 ? line - 2 : 1)),${line}p" "$file" | grep -q -- "$MARK"; then
		echo "  UNJUSTIFIED  ${hit:0:150}"
		found=1
	fi
done <<< "$hits"

if (( found )); then
	echo "EMITTER RESIDUAL: RED — a member lookup with no stated reason."
	echo "  Either the member belongs in CIR as a resolved memberRef, or mark the site '$MARK: <reason>'"
	echo "  and make sure the adjacent reason satisfies the external-member rule in docs/architecture.md."
	exit 1
fi
# Generic frames must come from CIR declarations. A frame minted in the delegate emitter may only be an encoding
# workaround and must say why at its site. Scanner calibration lives in test-emitter-residual.py: the actual
# source may legitimately contain no such workarounds. Same rule and same shape as the
# member-lookup check above: the marker sits at the site, not in a list that drifts from it.
FRAME_MARK='delegate-frame-residual'
scan_rc=0
hits="$(grep -nE '\.DefineGenericParameters\(' "$ROOT/toolchain/ilemit/Emitter.Delegates.cs")" || scan_rc=$?
if (( scan_rc > 1 )); then
	echo "EMITTER RESIDUAL: RED — delegate-frame source scan failed."
	exit 1
fi
while IFS= read -r hit; do
	[[ -n "$hit" ]] || continue
	line="${hit%%:*}"
	code="${hit#*:}"
	[[ "$code" =~ ^[[:space:]]*(//|\*) ]] && continue
	if ! sed -n "$((line > 2 ? line - 2 : 1)),${line}p" "$ROOT/toolchain/ilemit/Emitter.Delegates.cs" \
		| grep -q -- "$FRAME_MARK"; then
		echo "EMITTER RESIDUAL: RED — a generic frame is minted at Emitter.Delegates.cs:$line with no stated reason."
		echo "  A frame the emitter invents is a declaration CIR did not state. Either bir2cir authors it, or mark"
		echo "  the site '$FRAME_MARK: <encoding reason>'."
		exit 1
	fi
done <<< "$hits"
echo "EMITTER RESIDUAL: GREEN — every member lookup in ilemit states why the external-member rule allows it."
