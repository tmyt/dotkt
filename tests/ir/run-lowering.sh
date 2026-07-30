#!/usr/bin/env bash
# run-lowering.sh — LOWERING self-tests: synthetic BIR fed straight to bir2cir, asserted against the emitted CIR.
#
# The sibling gates check the corpus the compiler happens to produce. Some lowering RULES have no natural instance in
# it — not because they are unimportant, but because the producer has been fixed so the shape they guard no longer
# reaches them. A rule with no witness quietly stops being a rule; these documents are the witness.
#
# Two rules are covered today.
#
#   docs/dotkt-semantics.md §7a — a call-evaluation-plan binding that NOTHING reads is EVALUATED anyway, unless
#   evaluating it is genuinely unobservable (bir-common/ValueStability.cs, Q2). Two fixtures are the same plan either
#   side of that line — a `staticField` read (its declaring type's initializer can print, throw, mutate) and a `const`
#   — and they must lower differently.
#
#   docs/dotkt-semantics.md §7b — a slot whose type cannot be derived is a REFUSAL, never `kotlin.Any`. Those
#   refusals are invariant asserts: they cannot fire on the BIR the frontend produces, which is exactly why no
#   Kotlin source can witness them. The `reject-*` documents below are the witness, so the asserts cannot be
#   silently defeated by a later change that stops deriving (or stops checking).
#
# ACCEPT case — `<name>.bir.json` plus a `<name>.assert` file of lines:
#     +<substring>   the emitted CIR MUST contain it
#     -<substring>   the emitted CIR must NOT contain it
# Blank lines and `#` comments are ignored. The CIR is matched as its emitted JSON text with whitespace stripped.
#
# REJECT case — `reject-<name>.bir.json` plus a `reject-<name>.expected` file holding ONE substring per line: bir2cir
# must EXIT NONZERO on the document and its output must contain every substring. (Same shape as the schema/sanity
# self-tests in tests/ir/selftest/, and the same anti-vacuity rule: the lane needs at least one of each half.)
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
	if ! log="$(dotnet "$BIR2CIR_DLL" "$out" --compile-refs "$refs" "$bir" 2>&1)"; then
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
