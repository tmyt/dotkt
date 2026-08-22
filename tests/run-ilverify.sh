#!/usr/bin/env bash
# Formal IL verification for the categorized NUnit suites — run ONCE over each DotKt-emitted test
# assembly (not per-case). ilverify checks only the target assembly's own methods; the -r sets are resolution
# scopes only (the shared framework + the assembly's own output dir, which already holds NUnit/stdlib/producer).
# Whole-assembly verification replaces the former per-case, per-dll shell invocations.
#
# Green (exit 0) iff every ilverify finding matches one of the two narrow baselines below:
#
#   * ILVERIFY_XFAIL — a real, runtime-safe compiler defect awaiting a fix.
#   * ILVERIFY_UNVERIFIABLE — intentionally unverifiable ECMA-335 IL whose runtime behavior is separately tested.
#
# UNVERIFIABLE entries match only ILVerify's `[Unverifiable]` finding kind; a different error on the same method is
# a NEW-FAIL. Both maps use the same machine-readable "one reason per known finding" discipline.
#
# --audit-baseline additionally reddens on a DEAD key: a baseline entry that matched no finding at all has
# rotted into a mask for whatever finding lands on that method next. It is reported with scripts/lib.sh's strict
# xfail_diff wording and verdict, `FIXED … remove it from the xfail list`, and stays red until the entry is pruned.
# Opt-in because the audit is only meaningful over the COMPLETE emitted set: tests/packaged-sdk/run.sh verifies
# a two-assembly subset, where an unmatched key means "not in this subset", not "fixed".
# tests/run-nunit-tests.sh, which verifies every emitted suite assembly, passes it.
#
# Usage: tests/run-ilverify.sh [--audit-baseline] <emitted-test-assembly.dll> [<more.dll> ...]
set -euo pipefail

# Known runtime-safe compiler defects (substring -> reason). Keys are narrow fixture/method or
# emitted-type identifiers so they only mask the documented shape.
declare -A ILVERIFY_XFAIL=(
	# The LAST remaining position split of #86, and a REFERENCE one: `Array<T?>` erases to `object[]`
	# T-INDEPENDENTLY, while a concrete `Array<String?>` keeps its `string[]` because a reference `?` is not a physical
	# difference on the CLR (carrier-argument erasure moves possibly-VALUE arguments only). So
	# `arrayOf("x","y").copyOf(3)` hands back the `object[]` the open declaration promises, and its `toList()` yields an
	# `IReadOnlyList<object>` where the consumer's own slot is an `IReadOnlyCollection<string>`. Runtime-safe twice
	# over: `object` and `string` are reference-compatible, and the array `copyOf` built really IS a `string[]` (it
	# reflects on the receiver's element type), so the values are the declared ones; the RUN lane is green.
	#
	# Closing it means deciding the REFERENCE half of the same question the value half settled: whether `X?` in a
	# reified argument is `object` for a reference `X` too — which would make `List<String?>` an `IReadOnlyList<object>`
	# and cost every C# consumer the element type — or whether an open `Array<T?>`/`List<T?>` should instead keep the
	# type variable and box only at the value instantiations. Both are ABI decisions, neither is this fix's.
	["ArrayTests::copyOfGrowsWithNullTail()"]='an open Array<T?> is object[] T-independently while a concrete Array<String?> keeps string[], so copyOf().toList() yields an IReadOnlyList<object> where the slot is an IReadOnlyCollection<string> — runtime-safe (the array really is a string[]; RUN green); closing it needs the REFERENCE half of the carrier-argument decision'
)

# Intentionally unverifiable ECMA-335 IL (substring -> reason). These are not failed tests and not compiler defects:
# `localloc` produces a transient unmanaged pointer, so ILVerify correctly classifies its use as unverifiable. The
# runtime fixtures validate the resulting stack-buffer writes, reads, bounds behavior, Span interop and byref aliasing.
# The classifier below additionally requires `Error [Unverifiable]`, preventing these method keys from masking a
# StackUnexpected, DelegateCtor, or any other verification failure.
declare -A ILVERIFY_UNVERIFIABLE=(
	["StackBufferTests::stackAllocationAndSpanInterop()"]="by design: stackalloc emits localloc and transient unmanaged-pointer operations; runtime assertions validate the resulting buffer and Span behavior"
	["ByRefParameterTests::byrefOfAStackSlotEvaluatesItsIndexOnce()"]="by design: taking a stack-buffer slot by reference uses the same localloc-backed unmanaged pointer; runtime assertions validate aliasing and single evaluation"
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
declare -A MATCHED_XFAIL=()
declare -A MATCHED_UNVERIFIABLE=()

FINDING_CLASS=""
classify_finding() { # <finding line> -> 0 if classified, setting FINDING_CLASS and recording its key
	local line="$1" key
	FINDING_CLASS=""
	if [[ "$line" == *"Error [Unverifiable]"* ]]; then
		for key in "${!ILVERIFY_UNVERIFIABLE[@]}"; do
			[[ "$line" == *"$key"* ]] || continue
			MATCHED_UNVERIFIABLE["$key"]=1
			FINDING_CLASS="UNVERIFIABLE"
			return 0
		done
	fi
	for key in "${!ILVERIFY_XFAIL[@]}"; do
		[[ "$line" == *"$key"* ]] || continue
		MATCHED_XFAIL["$key"]=1
		FINDING_CLASS="XFAIL"
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
	declare -a newfails=() xfailed=() unverifiable=()
	for f in "${findings[@]}"; do
		if classify_finding "$f"; then
			case "$FINDING_CLASS" in
				XFAIL) xfailed+=("$f") ;;
				UNVERIFIABLE) unverifiable+=("$f") ;;
			esac
		else
			newfails+=("$f")
		fi
	done
	if (( ${#newfails[@]} == 0 )); then
		summary=""
		if (( ${#xfailed[@]} )); then summary="${#xfailed[@]} XFAIL"; fi
		if (( ${#unverifiable[@]} )); then
			[[ -z "$summary" ]] || summary+=", "
			summary+="${#unverifiable[@]} UNVERIFIABLE"
		fi
		[[ -z "$summary" ]] || summary="  ($summary finding(s), all baseline-listed)"
		echo "VERIFY  $(basename "$dll")$summary"
		for f in ${xfailed[@]+"${xfailed[@]}"}; do echo "    XFAIL: $f"; done
		for f in ${unverifiable[@]+"${unverifiable[@]}"}; do echo "    UNVERIFIABLE: $f"; done
	else
		echo "VERIFY FAIL  $(basename "$dll") — ${#newfails[@]} finding(s) outside the ILVERIFY_XFAIL/ILVERIFY_UNVERIFIABLE baselines:"
		for f in "${newfails[@]}"; do echo "    NEW-FAIL: $f"; done
		rc=1
	fi
	unset newfails xfailed unverifiable
done

# DEAD-KEY VERDICT: every baseline key that masked nothing over the complete emitted set. xfail_diff's wording,
# but red rather than its advisory green — see the header note on why this lane is deliberately stricter.
if (( audit )); then
	mapfile -t audit_keys < <(printf '%s\n' "${!ILVERIFY_XFAIL[@]}" | LC_ALL=C sort)
	for key in ${audit_keys[@]+"${audit_keys[@]}"}; do
		[[ -v MATCHED_XFAIL["$key"] ]] && continue
		echo "FIXED     ilverify:$key — fixed; remove it from the xfail list"
		rc=1
	done
	mapfile -t audit_keys < <(printf '%s\n' "${!ILVERIFY_UNVERIFIABLE[@]}" | LC_ALL=C sort)
	for key in ${audit_keys[@]+"${audit_keys[@]}"}; do
		[[ -v MATCHED_UNVERIFIABLE["$key"] ]] && continue
		echo "FIXED     ilverify-unverifiable:$key — no matching [Unverifiable] finding; remove it from the unverifiable list"
		rc=1
	done
fi
exit $rc
