#!/usr/bin/env bash
# run-sanity.sh — the OFFLINE IR-SANITY test corpus (#112 Phase 4).
#
# Runs scripts/verify-sanity.py — the build-free mirror of the in-process bir-common IrSanity gate
# (toolchain/bir-common/IrSanity.cs, run by BOTH bir2cir and ilemit) — over the FRESHLY-emitted BIR + CIR
# corpus. Where verify-schema checks document SHAPE, this checks MEANING (undeclared locals, dangling
# goto/brIf, missing field owners, malformed binOp/cond, bad for-cmp) and reddens on any violation.
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
