#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT_NAME=target-universe
source "$ROOT/scripts/lib.sh"

OUT="$ROOT/build/tests-target-universe"
rm -rf "$OUT"
mkdir -p "$OUT"

need_tool ilemit
need_tool bir2cir
need_tool retarget
need_dotnet_reference_sets
need_fe_klib
need_stdlib_ref
need_stdlib_rt

cli_fixture="$ROOT/tests/ir/selftest/accept-lowered-suspension.cir.json"
if cli_out="$(dotnet "$ILEMIT_DLL" "$OUT/cli-missing" MissingCompileRefs --runtime-refs "" "$cli_fixture" 2>&1)"; then
    die "ilemit accepted an invocation with no --compile-refs"
fi
grep -qF -- '--compile-refs is required' <<<"$cli_out" \
    || die "missing --compile-refs did not produce the calibrated diagnostic"
if cli_out="$(dotnet "$ILEMIT_DLL" "$OUT/cli-empty" EmptyCompileRefs --compile-refs "" --runtime-refs "" "$cli_fixture" 2>&1)"; then
    die "ilemit accepted an empty --compile-refs set"
fi
grep -qF 'the compile reference set is empty' <<<"$cli_out" \
    || die "empty --compile-refs did not fail at target-universe validation"

dotnet build "$ROOT/tests/target-universe/TargetUniverseProbe.ktproj" \
    -c Release -v:minimal --nologo \
    -p:BaseIntermediateOutputPath="$OUT/obj/" \
    -p:BaseOutputPath="$OUT/bin/" >/dev/null

probe_dir="$OUT/bin/Release/net10.0"
raw="$OUT/TargetUniverseProbe.raw.dll"
repaired="$OUT/TargetUniverseProbe.retargeted.dll"
cp "$probe_dir/TargetUniverseProbe.dll" "$raw"

actual="$(dotnet "$probe_dir/TargetUniverseProbe.dll")"
[[ "$actual" == "target-universe" ]] || die "raw emitted probe returned '$actual'"

dotnet "$RETARGET_DLL" "$raw" --out "$repaired" \
    --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL")" -v >"$OUT/retarget.log"
grep -qF 'no System.Private.CoreLib ref — already clean' "$OUT/retarget.log" \
    || die "retarget oracle changed raw target-scoped metadata"

dotnet run --project "$ROOT/tests/target-universe/MetadataProbe.csproj" -c Release -- \
    "$raw" "$repaired"
