#!/usr/bin/env bash
# Formal IL verification for the categorized NUnit suites — run ONCE over each DotKt-emitted test
# assembly (not per-case). ilverify checks only the target assembly's own methods; the -r sets are resolution
# scopes only (the shared framework + the assembly's own output dir, which already holds NUnit/stdlib/producer).
# Whole-assembly verification replaces the former per-case, per-dll shell invocations.
#
# Green (exit 0) iff every ilverify finding matches an ILVERIFY_XFAIL substring below — the same
# machine-readable "one reason per known finding" discipline. Any finding
# outside the baseline is a NEW-FAIL and reddens the gate.
#
# --audit-baseline additionally reddens on a DEAD key: a baseline entry that matched no finding at all has
# rotted into a mask for whatever finding lands on that method next. It is reported with scripts/lib.sh's
# xfail_diff wording, `FIXED … remove it from the xfail list`, but DELIBERATELY NOT with its verdict — where a
# FIXED line is green and merely advisory. A stale ilverify key is worse than a stale name in a fail-set,
# because it is a live substring filter over future findings, so this lane stays red until the entry is pruned.
# Opt-in because the audit is only meaningful over the COMPLETE emitted set: tests/packaged-sdk/run.sh verifies
# a two-assembly subset, where an unmatched key means "not in this subset", not "fixed".
# tests/run-nunit-tests.sh, which verifies every emitted suite assembly, passes it.
#
# Usage: tests/run-ilverify.sh [--audit-baseline] <emitted-test-assembly.dll> [<more.dll> ...]
set -euo pipefail

# Known runtime-safe, formal-only findings (substring -> tracking issue + reason). A finding line must contain
# one of these substrings to be tolerated. Keys are narrow fixture/method or emitted-type identifiers so they only mask
# the documented shape.
declare -A ILVERIFY_XFAIL=(
	# #86 (migrated ktproj-genq): a re-imported generic factory `holderOf(): Vault<T?>` whose bir2cir
	# NullableGenericErasure object-erases the nested Nullable(Tv) to `Vault<object>`; the [KotlinNullableGeneric]
	# round-trip restores `Vault<String?>` at the frontend, so the call's erased `Vault<object>` return meets the
	# consumer's restored `Vault<string>` slot — StackUnexpected. Runtime-SAFE (object/string are reference-compatible;
	# the erased Vault holds the string; the value-assert RUN lane is green). Same object-erasure formal-only family.
	["GenericMetadataRoundtripTests::nullableGenericMembersRoundTrip()"]="#86 nullable-generic object-erasure: holderOf's erased Vault<object> return vs the restored Vault<string> slot — runtime-safe (RUN green)"
	# ONE CAUSE, three methods. `Array<Int?>.toList()` yields an `IReadOnlyList<object>` — `copyOf` hands back the
	# `object[]` its `Array<T?>` return erases to, and the `toList` over it is instantiated at `object` — while the
	# consumer's slot is an `IReadOnlyCollection<!!0>`. The consumer's own type argument is NOT inferred from it,
	# because the type-argument unification pairs a declared and a flowed constructed type only when they are the same
	# DEFINITION, and those two heads are not. Pairing them by ARGUMENT POSITION would close these findings, and did,
	# until it was measured against `class Fixed<U> : Base<Int?>`: a `Fixed<object>` arriving at a `Base<T>` parameter
	# zips `T` to `object` although the argument is a `Base<Nullable<int32>>` and never was a `Base<object>` — which
	# resolves a member the emitted call does not have. Position-pairing is sound only within one definition; across
	# heads it needs the supertype walk to project the flowed type onto the declared head first. All three fixtures RUN
	# green: only object-level members are dispatched on the result.
	#
	# Keyed per method because the baseline is keyed per method, and each of these three fires for exactly this shape.
	# `copyOfGrowsWithNullTail` was SPLIT to get here: while both element kinds shared one method, its single entry
	# absorbed whichever shapes appeared under that name, and the REFERENCE-element one below was not visible at all.
	["ArrayTests::copyOfGrowsWithNullTailAtValueElements()"]='#86 D2: an Array<V?> at a VALUE element instantiates Array<T>.toList() at T=object, so its IReadOnlyList<object> meets an IReadOnlyCollection<Nullable<V>> slot — fires at V = Int, Long, Double and Char alike; runtime-safe (RUN green); the consumer type argument is not inferred across DIFFERENT generic heads, which needs a base-view projection to be sound'
	["ArrayTests::boxedGenericValues()"]='#86 D2: an Array<Int?> instantiates Array<T>.toList() at T=object, so its IReadOnlyList<object> meets an IReadOnlyCollection<Nullable<int32>> slot — runtime-safe (RUN green); the consumer type argument is not inferred across DIFFERENT generic heads, which needs a base-view projection to be sound'
	["ArrayTests::arrayOfNulls()"]='#86 D2: an Array<Int?> instantiates Array<T>.toList() at T=object, so its IReadOnlyList<object> meets an IReadOnlyCollection<Nullable<int32>> slot — runtime-safe (RUN green); the consumer type argument is not inferred across DIFFERENT generic heads, which needs a base-view projection to be sound'
	# The REFERENCE-element half of the same remainder, and a DIFFERENT observed shape: `Array<T?>` erases to `object[]`
	# T-INDEPENDENTLY, so `arrayOf("x","y").copyOf(3)` is an `object[]` too and its `toList()` meets a
	# `Collection<string>` rather than a `Collection<Nullable<int32>>`. Runtime-safe for a second reason as well as the
	# shared one: the array copyOf built really IS a `string[]` (it reflects on the receiver's element type), so the
	# values are the declared ones. Same closing condition as the three above.
	["ArrayTests::copyOfGrowsWithNullTail()"]='#86 D2: copyOf returns the object[] its Array<T?> erases to T-independently, so at a REFERENCE element its toList() yields an IReadOnlyList<object> where the slot is an IReadOnlyCollection<string> — runtime-safe (the array really is a string[]; RUN green); same base-view projection remainder as the value-element entries above'
	# #324: the value-element collection receiver conversion produces an `IEnumerable<object>` (all
	# `Enumerable.Cast<object>` can produce), which does not FORMALLY inhabit a `List<T?>` slot's
	# `IReadOnlyList<object>`. The conversion is now keyed correctly — on the receiver's own nullable element, not on
	# `typeArgs[0]` — so it no longer fires where it does not belong, and every assertion in the fixture RUNS green.
	# What is left is the interface-compatibility half named in #324: a wrapper that satisfies the list slot.
	["NullableTests::nullableGenericCollectionArgKeysOnTheReceiver()"]="#324 nullable value-element collection receiver: Enumerable.Cast<object> yields IEnumerable<object> where a List<T?> slot formally wants IReadOnlyList<object> — runtime-safe (RUN green)"
	# localloc is intentionally unverifiable ECMA-335 IL. The runtime test validates the resulting Span writes/reads.
	["StackBufferTests::stackAllocationAndSpanInterop()"]="by design: stackalloc emits localloc, which ILVerify must report as unverifiable; runtime assertions are green"
	["ByRefParameterTests::byrefOfAStackSlotEvaluatesItsIndexOnce()"]="by design: the same stackalloc/localloc unverifiability as its StackBufferTests sibling — this case takes the ADDRESS of a stack slot, so the pointer arithmetic is equally formal-only; runtime assertions are green"
)

ILV="$(find "$HOME/.dotnet" -name 'ILVerify.dll' 2>/dev/null | head -1)"
[[ -n "$ILV" ]] || { echo "ilverify: ILVerify.dll not found — install: dotnet tool install -g dotnet-ilverify"; exit 1; }
RTDIR="$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/* 2>/dev/null | sort -V | tail -1)"
[[ -d "$RTDIR" ]] || { echo "ilverify: Microsoft.NETCore.App shared framework not found"; exit 1; }

audit=0
declare -a DLLS=()
for arg in "$@"; do
	case "$arg" in
		--audit-baseline) audit=1 ;;
		-*) echo "run-ilverify: unknown option '$arg'" >&2; exit 2 ;;
		*) DLLS+=("$arg") ;;
	esac
done
(( ${#DLLS[@]} )) || { echo "run-ilverify: no assembly given" >&2; exit 2; }

# Which baseline keys actually masked a finding, accumulated across every verified assembly (a key is expected
# to match in exactly one of them). Read by the --audit-baseline dead-key verdict below.
declare -A MATCHED=()

is_xfail() { # <finding line> -> 0 if it matches a baseline substring (recording the key that matched)
	local line="$1" key
	for key in "${!ILVERIFY_XFAIL[@]}"; do
		[[ "$line" == *"$key"* ]] || continue
		MATCHED["$key"]=1
		return 0
	done
	return 1
}

rc=0
for dll in "${DLLS[@]}"; do
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

# DEAD-KEY VERDICT: every baseline key that masked nothing over the complete emitted set. xfail_diff's wording,
# but red rather than its advisory green — see the header note on why this lane is deliberately stricter.
if (( audit )); then
	mapfile -t audit_keys < <(printf '%s\n' "${!ILVERIFY_XFAIL[@]}" | LC_ALL=C sort)
	for key in ${audit_keys[@]+"${audit_keys[@]}"}; do
		[[ -v MATCHED["$key"] ]] && continue
		echo "FIXED     ilverify:$key — fixed; remove it from the xfail list"
		rc=1
	done
fi
exit $rc
