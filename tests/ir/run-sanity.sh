#!/usr/bin/env bash
# run-sanity.sh — the OFFLINE IR-SANITY test corpus (#112 Phase 4).
#
# Runs scripts/verify-sanity.py — the build-free mirror of the in-process bir-common IrSanity gate
# (toolchain/bir-common/IrSanity.cs, run by BOTH bir2cir and ilemit) — over the FRESHLY-emitted CIR corpus.
# Where verify-schema checks document SHAPE, this checks MEANING (undeclared locals, dangling goto/brIf,
# missing field owners, malformed binOp/cond, bad for-cmp, an un-lowered suspension) and reddens on any
# violation.
#
# It also runs the SELF-TESTS in tests/ir/selftest/ first (the `*.cir.json` half of that directory — the
# `*.bir.json` half belongs to run-schema.sh), for the checks whose whole point is a shape the emitted corpus
# never contains. Each fixture is asserted against BOTH implementations — the python mirror AND the NORMATIVE
# C# IrSanity compiled into ilemit — so a check deleted from one side cannot hide behind the other. See that
# directory's README.
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
SCRIPT_NAME=verify-sanity
source "$ROOT/scripts/lib.sh"
need_dotnet_reference_sets
# This harness intentionally executes rejecting fixtures and captures their non-zero status below. lib.sh enables
# fail-fast for normal build drivers; restore this script's original capture-oriented shell mode after using its
# authoritative targeting-pack discovery helper.
set +e
set +o pipefail
PY="${PYTHON:-python3}"
ILEMIT="build/ilemit-bin/ilemit.dll"
SELF_OUT="build/ir-selftest-out"

# SELF-TEST first (tests/ir/selftest/*.cir.json): the validators check whatever is on disk, so one that silently
# stopped checking would look exactly like a clean corpus. Each `reject-*.cir.json` is a shape the compiler must
# never emit and the validators must refuse, with the message its `.expected` file names; each `accept-*.cir.json`
# is the legitimate shape next door, so a validator that refused everything fails here too. Today this covers the
# SUSPENSION-LOWERED invariant (the emitted corpus contains no escaped suspension — that is the point — and the
# `mods.suspend` exemption it is calibrated against has no negative either) and the §2.7 STAMP-AGREEMENT invariant,
# whose subject `sty` does not survive into CIR at all: bir2cir checks that one on the PRE-lowering BIR
# (tests/ir/lowering/reject-stale-sty-after-passes pins the chokepoint), and the fixtures here are what hold both
# implementations of the relation to the same accepted-equivalence set.
#
# BOTH implementations are asserted. scripts/verify-sanity.py is only the corpus net; the NORMATIVE checker is the
# C# IrSanity compiled into ilemit, which is what actually stops a bad build. Asserting only the mirror would leave
# the normative half pinned by nothing but a comment. ilemit cannot fully EMIT a synthetic fixture (there are no
# refs to resolve its types against), so the accept side asserts the absence of a sanity diagnostic rather than a
# zero exit — the sanity gate runs at the head of EmitAssembly, ahead of any resolution.
echo "== verify-sanity: self-test (the checks with no natural negative in the corpus) =="
self_rc=0; n_reject=0; n_accept=0
rm -rf "$SELF_OUT"; mkdir -p "$SELF_OUT"
if [ ! -f "$ILEMIT" ]; then
  echo "  SELFTEST FAIL  $ILEMIT is missing — the normative C# half cannot be asserted (run 'make ilemit')"; self_rc=1
fi

for f in tests/ir/selftest/reject-*.cir.json; do
  [ -e "$f" ] || continue
  n_reject=$((n_reject + 1))
  exp="${f%.cir.json}.expected"
  # An absent/empty expectation would make `grep -F ""` match anything, degrading the assertion to "exited
  # non-zero" — which a JSON parse failure satisfies too.
  want="$(cat "$exp" 2>/dev/null)"
  if [ -z "$want" ]; then
    echo "  SELFTEST FAIL  $(basename "$f"): $(basename "$exp") is missing or empty (an empty expectation matches anything)"
    self_rc=1; continue
  fi
  out="$("$PY" scripts/verify-sanity.py "$f" 2>&1)"; frc=$?
  if [ $frc -eq 0 ]; then
    echo "  SELFTEST FAIL  $(basename "$f"): verify-sanity.py ACCEPTED a document it must refuse"; self_rc=1
  elif ! printf '%s' "$out" | grep -qF -- "$want"; then
    echo "  SELFTEST FAIL  $(basename "$f"): verify-sanity.py refused, but the message does not contain: $want"; self_rc=1
  else
    echo "  SELFTEST ok    $(basename "$f") (verify-sanity.py refused as documented)"
  fi
  if [ -f "$ILEMIT" ]; then
    iout="$(dotnet "$ILEMIT" "$SELF_OUT" IrSelftest --compile-refs "$FRAMEWORK_COMPILE_REFS" --runtime-refs "" "$f" 2>&1)"; irc=$?
    if [ $irc -eq 0 ]; then
      echo "  SELFTEST FAIL  $(basename "$f"): ilemit ACCEPTED a document it must refuse"; self_rc=1
    elif ! printf '%s' "$iout" | grep -qF -- "$want"; then
      echo "  SELFTEST FAIL  $(basename "$f"): ilemit refused, but the message does not contain: $want"; self_rc=1
      printf '%s\n' "$iout" | sed 's/^/                 /'
    else
      echo "  SELFTEST ok    $(basename "$f") (ilemit refused as documented)"
    fi
  fi
done

for f in tests/ir/selftest/accept-*.cir.json; do
  [ -e "$f" ] || continue
  n_accept=$((n_accept + 1))
  if "$PY" scripts/verify-sanity.py "$f" >/dev/null 2>&1; then
    echo "  SELFTEST ok    $(basename "$f") (verify-sanity.py accepted)"
  else
    echo "  SELFTEST FAIL  $(basename "$f"): verify-sanity.py REFUSED a well-formed document"; self_rc=1
    "$PY" scripts/verify-sanity.py "$f" 2>&1 | sed 's/^/                 /'
  fi
  if [ -f "$ILEMIT" ]; then
    iout="$(dotnet "$ILEMIT" "$SELF_OUT" IrSelftest --compile-refs "$FRAMEWORK_COMPILE_REFS" --runtime-refs "" "$f" 2>&1)"
    if printf '%s' "$iout" | grep -qF -- ": sanity: "; then
      echo "  SELFTEST FAIL  $(basename "$f"): ilemit raised a sanity violation on a well-formed document"; self_rc=1
      printf '%s\n' "$iout" | sed 's/^/                 /'
    else
      echo "  SELFTEST ok    $(basename "$f") (ilemit raised no sanity violation)"
    fi
  fi
done

# A lane that discovered nothing is indistinguishable from a lane that passed. Require one of EACH: an
# accept-only set would stay green with the checker deleted, a reject-only set with it stuck rejecting.
if [ $n_reject -eq 0 ] || [ $n_accept -eq 0 ]; then
  echo "  SELFTEST FAIL  found $n_reject reject / $n_accept accept fixture(s) in tests/ir/selftest/*.cir.json — the lane needs at least one of EACH or it asserts nothing"
  self_rc=1
fi
rm -rf "$SELF_OUT"
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
