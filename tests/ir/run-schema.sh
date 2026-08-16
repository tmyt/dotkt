#!/usr/bin/env bash
# run-schema.sh — the #37 BIR/CIR freeze ENFORCER test corpus.
#
# Runs the structural validator (scripts/verify-schema.py, normative schema docs/bir-cir.schema.json,
# spec docs/bir-cir-spec.md §5/§7) plus the #397 property-identity boundary validator over the
# FRESHLY-emitted BIR + CIR and reddens on any drift:
#   - a document type slot that is a bare string instead of a {t:...} node (types-are-nodes, §1);
#   - an unknown/typo'd/retired node kind {k} or type tag {t} (§2.5/§2.6);
#   - a malformed Type node, or an unknown mods key / vis value.
#   - inferred/reconstructed property identity, incomplete accessor associations, or BIR identity leaking into CIR.
#   - a newSuspendLambda whose physical receiver-first params diverge from canonical funcType.recv + funcType.params.
#   - a §2.7 call-evaluation-plan `bindRef` that resolves to no enclosing plan binding (the nesting rule).
#
# It also runs the SELF-TESTS in tests/ir/selftest/ first — synthetic documents the validator must refuse (and
# must accept), for the checks whose whole point is a shape the emitted corpus never contains. See that
# directory's README.
#
# COVERAGE = the whole pipeline surface:
#   - the CLR stdlib  build/clr-stdlib/{bir,cir}   (fresh after `make stdlib`) — 250 files, the bulk corpus;
#   - every categorized test project tests/**/obj/dotkt-{bir,cir}/*.json (fresh after verify-tests) — exercises
#     the language, CLR interop, coroutine-lowered, and cross-module kinds that the stdlib build does not;
#   - legacy one-shot developer outputs build/{bir,cir}-*/*.json, when present.
#
# FRESHNESS: this validates whatever is on disk, so run it AFTER a fresh emit. In the gate aggregate it runs
# AFTER verify-tests (which re-emits the test BIR/CIR); `make stdlib` refreshes the stdlib corpus. A stale tree
# with a retired spelling will (correctly) red — that IS drift.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 1
PY="${PYTHON:-python3}"

globs=()
[ -d build/clr-stdlib/bir ] && globs+=("build/clr-stdlib/bir/*.bir.json")
[ -d build/clr-stdlib/cir ] && globs+=("build/clr-stdlib/cir/*.cir.json")
while IFS= read -r -d '' file; do globs+=("$file"); done < <(
  find tests -type f \( -path '*/obj/dotkt-bir/*.bir.json' -o -path '*/obj/dotkt-cir/*.cir.json' \) -print0
)
for d in build/bir-*; do [ -d "$d" ] && globs+=("$d/*.bir.json"); done
for d in build/cir-*; do [ -d "$d" ] && globs+=("$d/*.cir.json"); done

# Accepted direct-lowering fixtures are authored BIR too. Include them in the #397 boundary lane so a fixture cannot
# carry a forbidden physical Property link merely because it bypasses kotc's emitted obj directories. Deliberate
# reject-* malformed inputs remain owned by run-lowering.sh and are not candidates for this positive contract.
property_globs=("${globs[@]}")
for file in tests/ir/lowering/*.bir.json; do
  [[ "$(basename "$file")" == reject-* ]] || property_globs+=("$file")
done
for file in tests/ir/lowering/*.bir-part.json; do
  property_globs+=("$file")
done

if [ ${#globs[@]} -eq 0 ]; then
  echo "SCHEMA GATE: no emitted BIR/CIR found — run 'make stdlib' and/or 'make verify-tests' first" >&2
  exit 2
fi

# The shared BIR/CIR codec (toolchain/bir-common: TypeNode, MemberRefNode) has no project and no test host of
# its own — it is <Compile Link/>-shared into each tool. `bir2cir --selftest` runs its round-trip and
# completeness assertions inside the EXACT compiled copy that reads and writes documents, so the contract and
# the reader that must honour it are checked together rather than one being assumed from the other.
echo "== verify-schema: shared BIR/CIR codec self-test (toolchain/bir-common) =="
BIR2CIR_DLL="build/bir2cir-bin/bir2cir.dll"
if [ ! -f "$BIR2CIR_DLL" ]; then
  echo "  SELFTEST FAIL  $BIR2CIR_DLL is missing — build the toolchain first (make bir2cir)"
  echo "SCHEMA GATE: RED (shared codec self-test)"; exit 1
fi
if ! dotnet "$BIR2CIR_DLL" --selftest; then
  echo "SCHEMA GATE: RED (shared codec self-test)"; exit 1
fi

# SELF-TEST next: the validator checks whatever is on disk, so one that silently stopped checking would look
# exactly like a clean corpus. Each `reject-*` is a shape the compiler must never emit and the validator must
# refuse, with the message its `.expected` file names; each `accept-*` is the legitimate shape next door, so a
# validator that refused everything fails here too. Two fixture sets, because the phase decides which document
# a shape is illegal in: tests/ir/selftest/*.bir.json (the §2.7 NESTING RULE, and the CIR-only vocabulary that
# must not appear in kotc BIR) and tests/ir/selftest-schema/*.cir.json (the resolved #370 memberRef, whose
# well-formed shape only exists in CIR). The sanity gate owns tests/ir/selftest/*.cir.json — a different
# validator — which is why the schema lane's CIR fixtures live in their own directory.
echo "== verify-schema: self-test (the checks with no natural negative in the corpus) =="
self_rc=0

# One fixture lane. Both halves are REQUIRED: an accept-only set would stay green with the checks deleted, a
# reject-only set with the validator stuck rejecting.
run_selftest_lane() {
  local dir="$1" ext="$2" n_reject=0 n_accept=0 f exp want out frc
  for f in "$dir"/reject-*"$ext"; do
    [ -e "$f" ] || continue
    n_reject=$((n_reject + 1))
    exp="${f%"$ext"}.expected"
    # An absent/empty expectation would make `grep -F ""` match anything, degrading the assertion to "exited
    # non-zero" — which a JSON parse failure satisfies too.
    want="$(cat "$exp" 2>/dev/null)"
    if [ -z "$want" ]; then
      echo "  SELFTEST FAIL  $(basename "$f"): $(basename "$exp") is missing or empty (an empty expectation matches anything)"
      self_rc=1; continue
    fi
    out="$("$PY" scripts/verify-schema.py "$f" 2>&1)"; frc=$?
    if [ $frc -eq 0 ]; then
      echo "  SELFTEST FAIL  $(basename "$f"): the validator ACCEPTED a document it must refuse"; self_rc=1
    elif ! printf '%s' "$out" | grep -qF -- "$want"; then
      echo "  SELFTEST FAIL  $(basename "$f"): refused, but the message does not contain: $want"; self_rc=1
    else
      echo "  SELFTEST ok    $(basename "$f") (refused as documented)"
    fi
  done
  for f in "$dir"/accept-*"$ext"; do
    [ -e "$f" ] || continue
    n_accept=$((n_accept + 1))
    if "$PY" scripts/verify-schema.py "$f" >/dev/null 2>&1; then
      echo "  SELFTEST ok    $(basename "$f") (accepted)"
    else
      echo "  SELFTEST FAIL  $(basename "$f"): the validator REFUSED a well-formed document"; self_rc=1
      "$PY" scripts/verify-schema.py "$f" 2>&1 | sed 's/^/                 /'
    fi
  done
  if [ $n_reject -eq 0 ] || [ $n_accept -eq 0 ]; then
    echo "  SELFTEST FAIL  found $n_reject reject / $n_accept accept fixture(s) in $dir/*$ext — the lane needs at least one of EACH or it asserts nothing"
    self_rc=1
  fi
}
run_selftest_lane tests/ir/selftest .bir.json
run_selftest_lane tests/ir/selftest-schema .cir.json
if [ $self_rc -ne 0 ]; then echo "SCHEMA GATE: RED (self-test)"; exit 1; fi

echo "== verify-schema: validating freshly-emitted BIR/CIR against the frozen #37 contract =="
"$PY" scripts/verify-schema.py "${globs[@]}"
rc=$?
if [ $rc -eq 0 ]; then
  echo "== verify-schema: enforcing the #397 one-way property-accessor identity boundary =="
  "$PY" scripts/verify-property-accessor-identity.py "${property_globs[@]}"
  rc=$?
fi
if [ $rc -eq 0 ]; then echo "SCHEMA GATE: GREEN"; else echo "SCHEMA GATE: RED (rc=$rc)"; fi
exit $rc
