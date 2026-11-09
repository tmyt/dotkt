#!/usr/bin/env bash
# Exercise the dev driver's resolved-reference wiring, not just dll2klib's batch API.
ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
SCRIPT_NAME=dev-wrapper-references
source "$ROOT/scripts/lib.sh"

need_kotc
need_tool dll2klib
need_tool bir2cir
need_tool ilemit
need_fe_klib
need_stdlib_ref
need_stdlib_rt

OUT="$ROOT/build/tests-dev-wrapper-references"
mkdir -p "$OUT"
fixtures="$ROOT/tests/special/dll2klib-e2e/wrapper"
driver="$ROOT/scripts/dotkt.sh"

# The enum exposes a runtime-owned interface, so projecting its consumer needs
# the implicit stdlib in the resolver catalog, without adding duplicate KLIBs.
bash "$driver" -o WrapperProducer -d "$OUT/producer" "$fixtures/producer.kt"
bash "$driver" --run -o WrapperConsumer -d "$OUT/implicit consumer" \
    --ref "$OUT/producer/WrapperProducer.dll" "$fixtures/consumer.kt" > "$OUT/implicit.log" 2>&1
grep -qx 'wrapper-enum-ok' "$OUT/implicit.log" || die "implicit stdlib consumer did not run"

# Disabling implicit stdlib must still allow the caller's authoritative refs.
bash "$driver" --no-stdlib --run -o WrapperExplicit -d "$OUT/explicit" \
    --ref "$STDLIB_REF_DLL" --ref "$STDLIB_RT_DLL" \
    --ref "$OUT/producer/WrapperProducer.dll" "$fixtures/consumer.kt" > "$OUT/explicit.log" 2>&1
grep -qx 'wrapper-enum-ok' "$OUT/explicit.log" || die "explicit stdlib consumer did not run"

info "implicit and --no-stdlib with explicit reference modes passed"
