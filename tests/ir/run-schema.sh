#!/usr/bin/env bash
# run-schema.sh — the #37 BIR/CIR freeze ENFORCER test corpus.
#
# Runs the structural validator (scripts/verify-schema.py, normative schema docs/bir-cir.schema.json,
# spec docs/bir-cir-spec.md §5/§7) over the FRESHLY-emitted BIR + CIR and reddens on any drift:
#   - a document type slot that is a bare string instead of a {t:...} node (types-are-nodes, §1);
#   - an unknown/typo'd/retired node kind {k} or type tag {t} (§2.5/§2.6);
#   - a malformed Type node, or an unknown mods key / vis value.
#   - a newSuspendLambda whose physical receiver-first params diverge from canonical funcType.recv + funcType.params.
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

echo "== verify-schema: validating freshly-emitted BIR/CIR against the frozen #37 contract =="
"$PY" scripts/verify-schema.py "${globs[@]}"
rc=$?
if [ $rc -eq 0 ]; then echo "SCHEMA GATE: GREEN"; else echo "SCHEMA GATE: RED (rc=$rc)"; fi
exit $rc
