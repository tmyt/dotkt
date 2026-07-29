#!/usr/bin/env bash
# run-sanity.sh — the OFFLINE IR-SANITY test corpus (#112 Phase 4).
#
# Runs scripts/verify-sanity.py — the build-free mirror of the in-process bir-common IrSanity gate
# (toolchain/bir-common/IrSanity.cs, run by BOTH bir2cir and ilemit) — over the FRESHLY-emitted BIR + CIR
# corpus. Where verify-schema checks document SHAPE, this checks MEANING (undeclared locals, dangling
# goto/brIf, missing field owners, malformed binOp/cond, bad for-cmp, an un-lowered suspension) and reddens
# on any violation.
#
# It also runs the SELF-TESTS in tests/ir/selftest/ first (the `*.cir.json` half of that directory — the
# `*.bir.json` half belongs to run-schema.sh), for the checks whose whole point is a shape the emitted corpus
# never contains. See that directory's README.
#
# COVERAGE = CIR ONLY (unlike verify-schema, which validates BIR + CIR shape): the CLR stdlib
# build/clr-stdlib/cir + every categorized test project tests/**/obj/dotkt-cir/*.cir.json, plus any legacy
# one-shot developer outputs build/cir-*/*.cir.json. The sanity invariants (local resolution,
# CFG targets) hold for POST-LOWERING CIR — the exact tree the in-process gate checks (bir2cir on its CIR
# output; ilemit at EmitAssembly). BIR is PRE-lowering: an inline-lambda body still references `it` and loop
# vars that bir2cir materializes as `var` statements during splice, so the local-resolution check
# legitimately (falsely) trips on BIR. FRESHNESS: run AFTER a fresh emit (verify-tests re-emits test CIR;
# `make stdlib` refreshes the stdlib CIR).
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 1
PY="${PYTHON:-python3}"

# SELF-TEST first (tests/ir/selftest/*.cir.json): the validator checks whatever is on disk, so one that silently
# stopped checking would look exactly like a clean corpus. Each `reject-*.cir.json` is a shape the compiler must
# never emit and the validator must refuse, with the message its `.expected` file names; each `accept-*.cir.json`
# is the legitimate shape next door, so a validator that refused everything fails here too. Today this covers the
# SUSPENSION-LOWERED invariant: the emitted corpus contains no escaped suspension (that is the point), and the
# `mods.suspend` exemption it is calibrated against has no negative either.
echo "== verify-sanity: self-test (the checks with no natural negative in the corpus) =="
self_rc=0
for f in tests/ir/selftest/reject-*.cir.json; do
  [ -e "$f" ] || continue
  want="$(cat "${f%.cir.json}.expected")"
  out="$("$PY" scripts/verify-sanity.py "$f" 2>&1)"; frc=$?
  if [ $frc -eq 0 ]; then
    echo "  SELFTEST FAIL  $(basename "$f"): the validator ACCEPTED a document it must refuse"; self_rc=1
  elif ! printf '%s' "$out" | grep -qF -- "$want"; then
    echo "  SELFTEST FAIL  $(basename "$f"): refused, but the message does not contain: $want"; self_rc=1
  else
    echo "  SELFTEST ok    $(basename "$f") (refused as documented)"
  fi
done
for f in tests/ir/selftest/accept-*.cir.json; do
  [ -e "$f" ] || continue
  if "$PY" scripts/verify-sanity.py "$f" >/dev/null 2>&1; then
    echo "  SELFTEST ok    $(basename "$f") (accepted)"
  else
    echo "  SELFTEST FAIL  $(basename "$f"): the validator REFUSED a well-formed document"; self_rc=1
    "$PY" scripts/verify-sanity.py "$f" 2>&1 | sed 's/^/                 /'
  fi
done
if [ $self_rc -ne 0 ]; then echo "SANITY GATE: RED (self-test)"; exit 1; fi

globs=()
[ -d build/clr-stdlib/cir ] && globs+=("build/clr-stdlib/cir/*.cir.json")
while IFS= read -r -d '' file; do globs+=("$file"); done < <(
  find tests -type f -path '*/obj/dotkt-cir/*.cir.json' -print0
)
for d in build/cir-*; do [ -d "$d" ] && globs+=("$d/*.cir.json"); done

if [ ${#globs[@]} -eq 0 ]; then
  echo "SANITY GATE: no emitted BIR/CIR found — run 'make stdlib' and/or 'make verify-tests' first" >&2
  exit 2
fi

echo "== verify-sanity: validating freshly-emitted CIR against the IR sanity invariants =="
"$PY" scripts/verify-sanity.py "${globs[@]}"
rc=$?
if [ $rc -eq 0 ]; then echo "SANITY GATE: GREEN"; else echo "SANITY GATE: RED (rc=$rc)"; fi
exit $rc
