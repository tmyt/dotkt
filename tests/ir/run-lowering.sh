#!/usr/bin/env bash
# run-lowering.sh — LOWERING self-tests: synthetic BIR fed straight to bir2cir, asserted against the emitted CIR.
#
# The sibling gates check the corpus the compiler happens to produce. Some lowering RULES have no natural instance in
# it — not because they are unimportant, but because the producer has been fixed so the shape they guard no longer
# reaches them. A rule with no witness quietly stops being a rule; these documents are the witness.
#
# The rules below are covered today.
#
#   docs/bir-cir-spec.md §2.7 — a pass that changes a node's RESULT TYPE rewrites or deletes its `sty`. bir2cir
#   checks this on the fully-passed BIR, just before BirTypeLowering strips the stamp, so the emitted CIR corpus
#   can never witness it and neither can Kotlin source (the violation is a pass bug, not a program).
#   `reject-stale-sty-after-passes` is that witness.
#
#   docs/dotkt-semantics.md §7a — a call-evaluation-plan binding that NOTHING reads is EVALUATED anyway, unless
#   evaluating it is genuinely unobservable (bir-common/ValueStability.cs, Q2). Two fixtures are the same plan either
#   side of that line — a `staticField` read (its declaring type's initializer can print, throw, mutate) and a `const`
#   — and they must lower differently.
#
#   bir2cir/NullableFlags.cs — the NRT byte walk carries a NULLABLE and an OBLIVIOUS marker, and states once that
#   oblivious wins. A position reached through both is `Oblivious(Nullable(T))`, which the FRONTEND cannot emit (its
#   oblivious wrapper always wraps a made-not-null type) and which no input in the corpus has yet produced through a
#   pass. `oblivious-over-nullable-byte` pins the bytes so the precedence cannot be re-decided arm by arm.
#
#   docs/dotkt-semantics.md §7b — a slot whose type cannot be derived is a REFUSAL, never `kotlin.Any`. Those
#   refusals are invariant asserts: they cannot fire on the BIR the frontend produces, which is exactly why no
#   Kotlin source can witness them. The `reject-*` documents below are the witness, so the asserts cannot be
#   silently defeated by a later change that stops deriving (or stops checking).
#
#   Project principle (layer ownership) — Kotlin const-field reads are resolved to literal CIR nodes by bir2cir.
#   `local-const-field-read` pins that decision boundary so ilemit never needs to reflect over fields and rediscover
#   whether a static field is a literal.
#
#   BIR result-frame ownership (#328) — a call result exactly matching its frontend `sty` is already instantiated in
#   the caller frame. `constructed-member-return-frame` runs that shape through both constructed-owner sweeps and
#   rejects the former nested re-substitution, including for a bir2cir-synthesized inherited-interface bridge, while
#   the await fixtures keep witnessing callee-relative substitution.
#
#   Nullable-generic reference-array results have a System.Array declaration slot but a reified concrete Kotlin
#   value. `nullable-tv-reference-array-return` pins the explicit System.Array -> String[] result projection so a following
#   generic use keeps its reference element instead of settling its method type argument to object.
#
#   A generic owner's companion statics live on one non-generic physical carrier, including while reference bodies are
#   retained long enough to emit field initializers. `reference-generic-static-self-init` pins the self-call owner.
#
#   Layer ownership (the void-to-value delegate adaptation) — a Kotlin `Unit` lambda lowers to a void-returning
#   delegate, and no method pointer is delegate-compatible with a slot whose `Invoke` returns. bir2cir authors the
#   adapter that produces the value; `unit-delegate-adapter` pins its whole physical shape — the newClosure over it,
#   the untouched natural construction as its capture, the adapter's constraint-free parameter-generic frame, and the
#   complete `kotlin.Unit.INSTANCE` field identity in its body — because the emitted metadata looks the same whether
#   CIR stated the adapter or an emitter synthesized one, which is exactly the confusion the rule ends.
#
#   bir-common/CollectionViewFaces.cs — a type naming a MUTABLE collection face also names its READ-ONLY sibling,
#   because the CLR does not derive one from the other. The emitted metadata cannot witness the rule: it looks the
#   same whether bir2cir stated the face or an emitter inferred it, which is exactly the confusion the rule ends.
#   `readonly-collection-view-sibling` asserts the stated array, including the two faces that owe nothing.
#
#   Layer ownership (collection-view value flow) — ordinary Kotlin collection values use opaque Object slots, while
#   exact native CLR sibling declarations stay reified. bir2cir materializes their checked value
#   conversions. The `collection-view-value-flow` fixture pins both directions across argument, local/field storage, conditional joins,
#   constructor evaluation/delegation, return, local-call result, and exact field/property result edges. It includes
#   fieldRef, constructed generic-field closure, and the exact physical result stamp inside each caller-facing cast;
#   ilemit emits those nodes one-to-one and owns no collection ABI inference.
#
#   Declaration/override identity (#444) — an ordinary member's @ClrIntrinsic allocation is selected by its complete
#   source declaration signature, method-generic arity, and constructed owner frame, never source name plus parameter
#   count. `exact-intrinsic-override-signature` uses a compiler-authored metadata reference with resolvable wrong
#   physical decoys to pin declarations, selected calls, and the referenced-interface fallback; a later CLR lookup
#   cannot make the test pass merely by rejecting the wrong name.
#
# ACCEPT case — `<name>.bir.json` plus a `<name>.assert` file of lines:
#     +<substring>   the emitted CIR MUST contain it
#     -<substring>   the emitted CIR must NOT contain it
#     ?<jq predicate> structural predicate over emitted files; ir_matches checks object subsets and ordered arrays
# Blank lines and `#` comments are ignored. The CIR is matched as its emitted JSON text with whitespace stripped.
#
# REJECT case — `reject-<name>.bir.json` plus a `reject-<name>.expected` file holding ONE substring per line: bir2cir
# must EXIT NONZERO on the document and its output must contain every substring. A module-wide refusal uses a
# `reject-multi-<name>.inputs` manifest whose non-comment lines name two or more `.bir-part.json` roots, plus the same
# `.expected` sibling. (Same anti-vacuity rule as the schema/sanity self-tests: the lane needs both accept and reject.)
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)/scripts/lib.sh"
cd "$ROOT"

need_tool bir2cir
need_dotnet_reference_sets
"${PYTHON:-python3}" tests/ir/test-lowering-patterns.py
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
if [[ ! -f "$STDLIB_REF_DLL" ]]; then
	echo "LOWERING GATE: RED — exact-intrinsic compiler metadata fixture requires $STDLIB_REF_DLL"
	exit 1
fi

# Compiler-owned metadata fixture for exact declaration/override binding. It deliberately contains a same-name,
# same-parameter-count intrinsic overload set and a physical decoy slot, so a name+arity lookup produces valid but
# wrong MethodImpl metadata instead of failing later by accident. Keep its build products out of the source tree.
exact_intrinsic_ref="$work/exact-intrinsic-ref"
if ! dotnet build tests/ir/reference-fixtures/exact-intrinsic-overloads/ExactIntrinsicOverloads.csproj \
	--nologo -v:q -o "$exact_intrinsic_ref" \
	-p:BaseIntermediateOutputPath="$work/exact-intrinsic-obj/" \
	-p:MSBuildProjectExtensionsPath="$work/exact-intrinsic-obj/" \
	-p:DotKtStdlibRef="$STDLIB_REF_DLL" >/dev/null; then
	echo "LOWERING GATE: RED — failed to build exact-intrinsic compiler metadata fixture"
	exit 1
fi

refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL")"
refs="$(refset_join "$refs" "$exact_intrinsic_ref/DotKt.Tests.ExactIntrinsicOverloads.dll")"

detected_jobs="$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf '1')"
(( detected_jobs > 4 )) && detected_jobs=4
lowering_jobs="${DOTKT_LOWERING_JOBS:-$detected_jobs}"
[[ "$lowering_jobs" =~ ^[1-9][0-9]*$ ]] || die "DOTKT_LOWERING_JOBS must be a positive integer (got '$lowering_jobs')"

run_fixture() {
	local fixture="$1" out="$2" name="$3" stem expected log text line bad=0 checks=0
	local -a inputs=() mode=()
	if [[ "$fixture" == *.inputs ]]; then
		stem="${fixture%.inputs}"
		mapfile -t inputs < <(sed -e '/^[[:space:]]*#/d' -e '/^[[:space:]]*$/d' "$fixture")
		if (( ${#inputs[@]} < 2 )); then
			echo "  LOWERING FAIL  $name: multi-root manifest needs at least two inputs"
			return 1
		fi
	else
		stem="${fixture%.bir.json}"
		inputs=("$fixture")
	fi

	mkdir -p "$out"
	if [[ "$name" == reject-* ]]; then
		expected="$stem.expected"
		if [[ ! -f "$expected" ]] || ! grep -q '[^[:space:]]' "$expected"; then
			echo "  LOWERING FAIL  $name: no non-empty .expected file"
			return 1
		fi
		if log="$(dotnet "$BIR2CIR_DLL" "$out" --compile-refs "$refs" "${inputs[@]}" 2>&1)"; then
			echo "  LOWERING FAIL  $name: bir2cir ACCEPTED a document it must refuse"
			printf '%s\n' "$log" | sed 's/^/                 /'
			return 1
		fi
		while IFS= read -r line || [[ -n "$line" ]]; do
			[[ "$line" =~ ^[[:space:]]*$ || "$line" == '#'* ]] && continue
			(( ++checks ))
			if [[ "$log" != *"$line"* ]]; then
				echo "  LOWERING FAIL  $name: the refusal is MISSING $line"
				bad=1
			fi
		done < "$expected"
		if (( checks == 0 )); then
			echo "  LOWERING FAIL  $name: no effective diagnostic assertion"
			bad=1
		fi
		if (( bad )); then
			printf '%s\n' "$log" | sed 's/^/                 /'
			return 1
		fi
		echo "  LOWERING ok    $name (refused)"
		return 0
	fi

	expected="$stem.assert"
	if [[ ! -f "$expected" ]] || ! grep -qE '^[+?-].' "$expected"; then
		echo "  LOWERING FAIL  $name: no effective .assert file"
		return 1
	fi
	if [[ "$fixture" == *.bir.json && -f "$stem.args" ]]; then
		mapfile -t mode < <(sed -e '/^[[:space:]]*#/d' -e '/^[[:space:]]*$/d' "$stem.args")
	fi
	if ! log="$(dotnet "$BIR2CIR_DLL" "$out" --compile-refs "$refs" "${mode[@]}" "${inputs[@]}" 2>&1)"; then
		echo "  LOWERING FAIL  $name: bir2cir refused the document"
		printf '%s\n' "$log" | sed 's/^/                 /'
		return 1
	fi
	if ! text="$(cat "$out"/*.cir.json | tr -d ' \n\t')" || [[ -z "$text" ]]; then
		echo "  LOWERING FAIL  $name: bir2cir did not emit CIR"
		return 1
	fi
	while IFS= read -r line || [[ -n "$line" ]]; do
		case "$line" in
			''|'#'*) continue ;;
			'+'*) if [[ "$text" != *"${line:1}"* ]]; then
				echo "  LOWERING FAIL  $name: the CIR is MISSING ${line:1}"; bad=1
			fi ;;
			'-'*) if [[ "$text" == *"${line:1}"* ]]; then
				echo "  LOWERING FAIL  $name: the CIR CONTAINS ${line:1}"; bad=1
			fi ;;
			'?'*) if ! jq -L "$ROOT/tests/ir" -s -e 'include "lowering-pattern"; '"${line:1}" "$out"/*.cir.json >/dev/null; then
				echo "  LOWERING FAIL  $name: CIR predicate failed: ${line:1}"; bad=1
			fi ;;
			*) echo "  LOWERING FAIL  $name: malformed assertion line: $line"; bad=1 ;;
		esac
	done < "$expected"
	if (( bad )); then
		printf '%s\n' "$text" | sed 's/^/                 /'
		return 1
	fi
	echo "  LOWERING ok    $name"
}

# Keep each compiler invocation isolated. Reap and print in fixture order, not
# completion order, so concurrent compiler output cannot interleave diagnostics.
declare -a pids=() logs=() names=()
rc=0
cases=0
rejects=0
reap_fixture() {
	local status=0
	wait "${pids[0]}" || status=$?
	if (( status != 0 )); then
		rc=1
		if (( status != 1 )) || [[ ! -s "${logs[0]}" ]]; then
			echo "  LOWERING FAIL  ${names[0]}: worker exited with status $status"
		fi
	fi
	cat "${logs[0]}"
	pids=("${pids[@]:1}")
	logs=("${logs[@]:1}")
	names=("${names[@]:1}")
}

queue_fixture() {
	local fixture="$1" name out
	[[ -e "$fixture" ]] || return 0
	name="${fixture##*/}"
	if [[ "$fixture" == *.inputs ]]; then name="${name%.inputs}"; else name="${name%.bir.json}"; fi
	if [[ "$name" == reject-* ]]; then (( ++rejects )); else (( ++cases )); fi
	out="$work/case-$((cases + rejects))"
	run_fixture "$fixture" "$out" "$name" > "$out.log" 2>&1 &
	pids+=("$!")
	logs+=("$out.log")
	names+=("$name")
	if (( ${#pids[@]} >= lowering_jobs )); then reap_fixture; fi
}

echo "== verify-lowering: synthetic BIR -> bir2cir -> CIR assertions (jobs=$lowering_jobs) =="
for fixture in tests/ir/lowering/reject-*.bir.json tests/ir/lowering/reject-multi-*.inputs tests/ir/lowering/accept-multi-*.inputs; do
	queue_fixture "$fixture"
done
for fixture in tests/ir/lowering/*.bir.json; do
	[[ "${fixture##*/}" == reject-* ]] && continue
	queue_fixture "$fixture"
done
while (( ${#pids[@]} )); do reap_fixture; done

if [[ $cases -eq 0 || $rejects -eq 0 ]]; then
	echo "  LOWERING FAIL  found $cases accept / $rejects reject fixture(s) under tests/ir/lowering/ — the lane needs at least one of EACH or a half asserts nothing"
	rc=1
fi
if [[ $rc -eq 0 ]]; then echo "LOWERING GATE: GREEN ($cases accept + $rejects reject fixture(s))"; else echo "LOWERING GATE: RED"; fi
exit $rc
