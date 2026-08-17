#!/usr/bin/env bash
# run-lowering.sh — LOWERING self-tests: synthetic BIR fed straight to bir2cir, asserted against the emitted CIR.
#
# The sibling gates check the corpus the compiler happens to produce. Some lowering RULES have no natural instance in
# it — not because they are unimportant, but because the producer has been fixed so the shape they guard no longer
# reaches them. A rule with no witness quietly stops being a rule; these documents are the witness.
#
# Eight rules are covered today.
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
#   A generic owner's companion statics live on one non-generic physical carrier, including while reference bodies are
#   retained long enough to emit field initializers. `reference-generic-static-self-init` pins the self-call owner.
#
#   Layer ownership (the suspend residue) — a `suspend` declaration the cold lowering does not admit gets its physical
#   body from bir2cir, and in an APP build there is no such declaration at all, so one is a cold-lowering miss and is
#   refused there rather than reaching an emitter that would have to invent a body for it.
#   `reject-unlowered-suspend-declaration` is that refusal's witness; no Kotlin source produces the shape (the admit
#   gate it trips is the retired kotc CPS path), which is why it has to be authored here.

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
# ACCEPT case — `<name>.bir.json` plus a `<name>.assert` file of lines:
#     +<substring>   the emitted CIR MUST contain it
#     -<substring>   the emitted CIR must NOT contain it
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
refs="$FRAMEWORK_COMPILE_REFS"
if [[ -f "$STDLIB_REF_DLL" ]]; then refs="$(refset_join "$refs" "$STDLIB_REF_DLL")"; fi

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
rc=0
cases=0
rejects=0
echo "== verify-lowering: synthetic BIR -> bir2cir -> CIR assertions =="

# --- REJECT half: bir2cir MUST refuse the document, with the expected wording ------------------------------------
for bir in tests/ir/lowering/reject-*.bir.json; do
	[[ -e "$bir" ]] || continue
	rejects=$((rejects + 1))
	name="$(basename "$bir" .bir.json)"
	expected="${bir%.bir.json}.expected"
	if [[ ! -f "$expected" ]] || ! grep -q '[^[:space:]]' "$expected"; then
		echo "  LOWERING FAIL  $name: no non-empty .expected file — a refusal fixture that pins no wording asserts nothing"
		rc=1; continue
	fi
	out="$work/$name"; mkdir -p "$out"
	if log="$(dotnet "$BIR2CIR_DLL" "$out" --compile-refs "$refs" "$bir" 2>&1)"; then
		echo "  LOWERING FAIL  $name: bir2cir ACCEPTED a document it must refuse"
		printf '%s\n' "$log" | sed 's/^/                 /'
		rc=1; continue
	fi
	bad=0
	while IFS= read -r line || [[ -n "$line" ]]; do
		case "$line" in
			''|'#'*) continue ;;
			*) if ! printf '%s' "$log" | grep -qF -- "$line"; then
					echo "  LOWERING FAIL  $name: the refusal is MISSING $line"; bad=1
				fi ;;
		esac
	done < "$expected"
	if [[ $bad -ne 0 ]]; then rc=1; printf '%s\n' "$log" | sed 's/^/                 /'; else echo "  LOWERING ok    $name (refused)"; fi
done

# A module-wide invariant cannot be witnessed by one document: all BIR roots contribute TypeDefs to one CLR module.
for manifest in tests/ir/lowering/reject-multi-*.inputs; do
	[[ -e "$manifest" ]] || continue
	rejects=$((rejects + 1))
	name="$(basename "$manifest" .inputs)"
	expected="${manifest%.inputs}.expected"
	mapfile -t inputs < <(sed -e '/^[[:space:]]*#/d' -e '/^[[:space:]]*$/d' "$manifest")
	if [[ ${#inputs[@]} -lt 2 ]]; then
		echo "  LOWERING FAIL  $name: multi-root manifest needs at least two inputs"; rc=1; continue
	fi
	if [[ ! -f "$expected" ]] || ! grep -q '[^[:space:]]' "$expected"; then
		echo "  LOWERING FAIL  $name: no non-empty .expected file"; rc=1; continue
	fi
	out="$work/$name"; mkdir -p "$out"
	if log="$(dotnet "$BIR2CIR_DLL" "$out" --compile-refs "$refs" "${inputs[@]}" 2>&1)"; then
		echo "  LOWERING FAIL  $name: bir2cir ACCEPTED a multi-root module it must refuse"
		printf '%s\n' "$log" | sed 's/^/                 /'
		rc=1; continue
	fi
	bad=0
	while IFS= read -r line || [[ -n "$line" ]]; do
		case "$line" in
			''|'#'*) continue ;;
			*) if ! printf '%s' "$log" | grep -qF -- "$line"; then
					echo "  LOWERING FAIL  $name: the refusal is MISSING $line"; bad=1
				fi ;;
		esac
	done < "$expected"
	if [[ $bad -ne 0 ]]; then rc=1; printf '%s\n' "$log" | sed 's/^/                 /'; else echo "  LOWERING ok    $name (refused)"; fi
done

# A module-wide accept rule can likewise depend on more than one source root (for example, a frontend-resolved
# file-facade owner must distinguish otherwise identical top-level declarations). Its manifest and assertion syntax
# mirror the reject-multi lane, but successful lowering is required.
for manifest in tests/ir/lowering/accept-multi-*.inputs; do
	[[ -e "$manifest" ]] || continue
	cases=$((cases + 1))
	name="$(basename "$manifest" .inputs)"
	assert="${manifest%.inputs}.assert"
	mapfile -t inputs < <(sed -e '/^[[:space:]]*#/d' -e '/^[[:space:]]*$/d' "$manifest")
	if [[ ${#inputs[@]} -lt 2 ]]; then
		echo "  LOWERING FAIL  $name: multi-root manifest needs at least two inputs"; rc=1; continue
	fi
	if [[ ! -f "$assert" ]] || ! grep -qE '^[+-].' "$assert"; then
		echo "  LOWERING FAIL  $name: no effective .assert file"; rc=1; continue
	fi
	out="$work/$name"; mkdir -p "$out"
	if ! log="$(dotnet "$BIR2CIR_DLL" "$out" --compile-refs "$refs" "${inputs[@]}" 2>&1)"; then
		echo "  LOWERING FAIL  $name: bir2cir refused the multi-root document"
		printf '%s\n' "$log" | sed 's/^/                 /'
		rc=1; continue
	fi
	cir="$(cat "$out"/*.cir.json | tr -d ' \n\t')"
	bad=0
	while IFS= read -r line || [[ -n "$line" ]]; do
		case "$line" in
			''|'#'*) continue ;;
			'+'*) if ! printf '%s' "$cir" | grep -qF -- "${line:1}"; then
					echo "  LOWERING FAIL  $name: the CIR is MISSING ${line:1}"; bad=1
				fi ;;
			'-'*) if printf '%s' "$cir" | grep -qF -- "${line:1}"; then
					echo "  LOWERING FAIL  $name: the CIR CONTAINS ${line:1}"; bad=1
				fi ;;
			*) echo "  LOWERING FAIL  $name: malformed assertion line: $line"; bad=1 ;;
		esac
	done < "$assert"
	if [[ $bad -ne 0 ]]; then rc=1; printf '%s\n' "$cir" | sed 's/^/                 /'; else echo "  LOWERING ok    $name"; fi
done

# --- ACCEPT half: bir2cir lowers the document and the CIR satisfies the +/- assertions ---------------------------
for bir in tests/ir/lowering/*.bir.json; do
	[[ -e "$bir" ]] || continue
	case "$(basename "$bir")" in reject-*) continue ;; esac
	cases=$((cases + 1))
	name="$(basename "$bir" .bir.json)"
	assert="${bir%.bir.json}.assert"
	# ANTI-VACUITY, the same guards the schema self-tests carry: a lane that silently checks nothing looks exactly
	# like a lane that passes. A fixture with no assertion file, or one holding no effective +/- line, is RED —
	# never "ok" — and a lane with no fixture at all is RED at the end.
	if [[ ! -f "$assert" ]]; then
		echo "  LOWERING FAIL  $name: no .assert file — a fixture that asserts nothing cannot pass"
		rc=1; continue
	fi
	if ! grep -qE '^[+-].' "$assert"; then
		echo "  LOWERING FAIL  $name: .assert holds no effective +/- assertion"
		rc=1; continue
	fi
	out="$work/$name"; mkdir -p "$out"
	mode=()
	args_file="${bir%.bir.json}.args"
	if [[ -f "$args_file" ]]; then
		mapfile -t mode < <(sed -e '/^[[:space:]]*#/d' -e '/^[[:space:]]*$/d' "$args_file")
	fi
	if ! log="$(dotnet "$BIR2CIR_DLL" "$out" --compile-refs "$refs" "${mode[@]}" "$bir" 2>&1)"; then
		echo "  LOWERING FAIL  $name: bir2cir refused the document"
		printf '%s\n' "$log" | sed 's/^/                 /'
		rc=1; continue
	fi
	cir="$(cat "$out"/*.cir.json | tr -d ' \n\t')"
	bad=0
	while IFS= read -r line || [[ -n "$line" ]]; do
		case "$line" in
			''|'#'*) continue ;;
			'+'*) if ! printf '%s' "$cir" | grep -qF -- "${line:1}"; then
					echo "  LOWERING FAIL  $name: the CIR is MISSING ${line:1}"; bad=1
				fi ;;
			'-'*) if printf '%s' "$cir" | grep -qF -- "${line:1}"; then
					echo "  LOWERING FAIL  $name: the CIR CONTAINS ${line:1}"; bad=1
				fi ;;
			*) echo "  LOWERING FAIL  $name: malformed assertion line: $line"; bad=1 ;;
		esac
	done < "$assert"
	if [[ $bad -ne 0 ]]; then rc=1; printf '%s\n' "$cir" | sed 's/^/                 /'; else echo "  LOWERING ok    $name"; fi
done
if [[ $cases -eq 0 || $rejects -eq 0 ]]; then
	echo "  LOWERING FAIL  found $cases accept / $rejects reject fixture(s) under tests/ir/lowering/ — the lane needs at least one of EACH or a half asserts nothing"
	rc=1
fi
if [[ $rc -eq 0 ]]; then echo "LOWERING GATE: GREEN ($cases accept + $rejects reject fixture(s))"; else echo "LOWERING GATE: RED"; fi
exit $rc
