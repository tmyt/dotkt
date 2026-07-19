#!/usr/bin/env bash
# Formal IL verification (ilverify) for the NUnit test-harness pilot — run ONCE over each DotKt-EMITTED test
# assembly (not per-case). ilverify checks only the target assembly's own methods; the -r sets are resolution
# scopes only (the shared framework + the assembly's own output dir, which already holds NUnit/stdlib/producer).
# This is the whole-assembly equivalent of verify-il.sh's per-dll ilverify phase: 1 invocation instead of N.
#
# Green (exit 0) iff every ilverify finding matches an ILVERIFY_XFAIL substring below — the same
# machine-readable "one reason per known finding" discipline as verify-il.sh's XFAIL_ILVERIFY map. Any finding
# outside the baseline is a NEW-FAIL and reddens the gate.
#
# Usage: tests/run-ilverify.sh <emitted-test-assembly.dll> [<more.dll> ...]
set -euo pipefail

# Known runtime-safe, formal-only findings (substring -> tracking issue + reason). A finding line must contain
# one of these substrings to be tolerated. Keyed narrowly (fixture::method + finding kind) so it can only mask
# the exact known shape. Mirror of verify-il.sh XFAIL_ILVERIFY entries, re-expressed for the battery methods.
declare -A ILVERIFY_XFAIL=(
	# #170/#150: joinToString{} trailing-lambda synthetic delegate — ilverify rejects the delegate .ctor args;
	# runtime-safe (the value-assert RUN lane is green). Same finding the verify-il.sh [defargs] entry carries.
	["CollectionsDefaultArgsTests::joinToStringDefaults()"]="#170/#150 formal-only DelegateCtor on a joinToString{} synthetic delegate — runtime-safe (RUN green)"
)

ILV="$(find "$HOME/.dotnet" -name 'ILVerify.dll' 2>/dev/null | head -1)"
[[ -n "$ILV" ]] || { echo "ilverify: ILVerify.dll not found — install: dotnet tool install -g dotnet-ilverify"; exit 1; }
RTDIR="$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/* 2>/dev/null | sort -V | tail -1)"
[[ -d "$RTDIR" ]] || { echo "ilverify: Microsoft.NETCore.App shared framework not found"; exit 1; }

is_xfail() { # <finding line> -> 0 if it matches a baseline substring
	local line="$1" key
	for key in "${!ILVERIFY_XFAIL[@]}"; do [[ "$line" == *"$key"* ]] && return 0; done
	return 1
}

rc=0
for dll in "$@"; do
	[[ -f "$dll" ]] || { echo "ilverify: MISSING $dll"; rc=1; continue; }
	bindir="$(dirname "$dll")"
	out="$(dotnet "$ILV" "$dll" -r "$RTDIR/*.dll" -r "$bindir/*.dll" 2>&1 || true)"
	# Finding lines look like:  [IL]: Error [Kind]: [<asm> : Fixture::method()][offset ...] <msg>
	mapfile -t findings < <(grep -E '\[IL\]: Error|Error \[' <<<"$out" || true)
	declare -a newfails=() xfailed=()
	for f in "${findings[@]}"; do
		if is_xfail "$f"; then xfailed+=("$f"); else newfails+=("$f"); fi
	done
	if (( ${#newfails[@]} == 0 )); then
		echo "VERIFY  $(basename "$dll")${xfailed[0]:+  (${#xfailed[@]} XFAIL finding(s), all baseline-listed)}"
		for f in ${xfailed[@]+"${xfailed[@]}"}; do echo "    XFAIL: $f"; done
	else
		echo "VERIFY FAIL  $(basename "$dll") — ${#newfails[@]} finding(s) outside the ILVERIFY_XFAIL baseline:"
		for f in "${newfails[@]}"; do echo "    NEW-FAIL: $f"; done
		rc=1
	fi
	unset newfails xfailed
done
exit $rc
