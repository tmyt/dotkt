#!/usr/bin/env bash
# run-lowering.sh — LOWERING self-tests: synthetic BIR fed straight to bir2cir, asserted against the emitted CIR.
#
# The sibling gates check the corpus the compiler happens to produce. Some lowering RULES have no natural instance in
# it — not because they are unimportant, but because the producer has been fixed so the shape they guard no longer
# reaches them. A rule with no witness quietly stops being a rule; these documents are the witness.
#
# Today this covers ONE rule, docs/dotkt-semantics.md §7a: a call-evaluation-plan binding that NOTHING reads is
# EVALUATED anyway, unless evaluating it is genuinely unobservable (bir-common/ValueStability.cs, Q2). The two
# fixtures are the same plan either side of that line — a `staticField` read (its declaring type's initializer can
# print, throw, mutate) and a `const` — and they must lower differently.
#
# Each case is `<name>.bir.json` plus a `<name>.assert` file of lines:
#     +<substring>   the emitted CIR MUST contain it
#     -<substring>   the emitted CIR must NOT contain it
# Blank lines and `#` comments are ignored. The CIR is matched as its emitted JSON text with whitespace stripped.
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)/scripts/lib.sh"
cd "$ROOT"

need_tool bir2cir
need_dotnet_reference_sets
refs="$FRAMEWORK_COMPILE_REFS"
if [[ -f "$STDLIB_REF_DLL" ]]; then refs="$(refset_join "$refs" "$STDLIB_REF_DLL")"; fi

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
rc=0
cases=0
echo "== verify-lowering: synthetic BIR -> bir2cir -> CIR assertions =="
for bir in tests/ir/lowering/*.bir.json; do
	[[ -e "$bir" ]] || continue
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
if [[ $cases -eq 0 ]]; then
	echo "  LOWERING FAIL  no fixture found under tests/ir/lowering/ — the lane would pass vacuously"
	rc=1
fi
if [[ $rc -eq 0 ]]; then echo "LOWERING GATE: GREEN ($cases fixture(s))"; else echo "LOWERING GATE: RED"; fi
exit $rc
