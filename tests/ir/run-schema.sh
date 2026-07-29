#!/usr/bin/env bash
# run-schema.sh — the #37 BIR/CIR freeze ENFORCER test corpus.
#
# Runs the structural validator (scripts/verify-schema.py, normative schema docs/bir-cir.schema.json,
# spec docs/bir-cir-spec.md §5/§7) over the FRESHLY-emitted BIR + CIR and reddens on any drift:
#   - a document type slot that is a bare string instead of a {t:...} node (types-are-nodes, §1);
#   - an unknown/typo'd/retired node kind {k} or type tag {t} (§2.5/§2.6);
#   - a malformed Type node, or an unknown mods key / vis value.
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

if [ ${#globs[@]} -eq 0 ]; then
  echo "SCHEMA GATE: no emitted BIR/CIR found — run 'make stdlib' and/or 'make verify-tests' first" >&2
  exit 2
fi

# SELF-TEST first (tests/ir/selftest/): the validator checks whatever is on disk, so one that silently stopped
# checking would look exactly like a clean corpus. Each `reject-*.bir.json` is a shape the compiler must never
# emit and the validator must refuse, with the message its `.expected` file names; each `accept-*.bir.json` is
# the legitimate shape next door, so a validator that refused everything fails here too. Today this covers the
# §2.7 NESTING RULE, whose whole purpose is a shape the emitted corpus never contains.
echo "== verify-schema: self-test (the checks with no natural negative in the corpus) =="
self_rc=0
for f in tests/ir/selftest/reject-*.bir.json; do
  [ -e "$f" ] || continue
  want="$(cat "${f%.bir.json}.expected")"
  out="$("$PY" scripts/verify-schema.py "$f" 2>&1)"; frc=$?
  if [ $frc -eq 0 ]; then
    echo "  SELFTEST FAIL  $(basename "$f"): the validator ACCEPTED a document it must refuse"; self_rc=1
  elif ! printf '%s' "$out" | grep -qF -- "$want"; then
    echo "  SELFTEST FAIL  $(basename "$f"): refused, but the message does not contain: $want"; self_rc=1
  else
    echo "  SELFTEST ok    $(basename "$f") (refused as documented)"
  fi
done
for f in tests/ir/selftest/accept-*.bir.json; do
  [ -e "$f" ] || continue
  if "$PY" scripts/verify-schema.py "$f" >/dev/null 2>&1; then
    echo "  SELFTEST ok    $(basename "$f") (accepted)"
  else
    echo "  SELFTEST FAIL  $(basename "$f"): the validator REFUSED a well-formed document"; self_rc=1
    "$PY" scripts/verify-schema.py "$f" 2>&1 | sed 's/^/                 /'
  fi
done
if [ $self_rc -ne 0 ]; then echo "SCHEMA GATE: RED (self-test)"; exit 1; fi

echo "== verify-schema: validating freshly-emitted BIR/CIR against the frozen #37 contract =="
"$PY" scripts/verify-schema.py "${globs[@]}"
rc=$?
if [ $rc -eq 0 ]; then echo "SCHEMA GATE: GREEN"; else echo "SCHEMA GATE: RED (rc=$rc)"; fi
exit $rc
