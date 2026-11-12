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
    -c Release -v:minimal --nologo -p:Deterministic=true \
    -p:BaseIntermediateOutputPath="$OUT/obj/" \
    -p:BaseOutputPath="$OUT/bin/" >/dev/null

probe_dir="$OUT/bin/Release/net10.0"
raw="$OUT/TargetUniverseProbe.raw.dll"
cp "$probe_dir/TargetUniverseProbe.dll" "$raw"

actual="$(dotnet "$probe_dir/TargetUniverseProbe.dll")"
[[ "$actual" == "target-universe" ]] || die "raw emitted probe returned '$actual'"

# Reuse this real Kotlin compilation: only lower once more for the alternate codec.
# Both emitters receive the same physical graph, assembly identity and target universe.
dotnet "$BIR2CIR_DLL" "$OUT/msgpack-cir" --cir-format=msgpack \
    --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$STDLIB_RT_DLL")" \
    "$OUT"/obj/Release/net10.0/bir/*.bir.json
for format in json msgpack; do
    if [[ "$format" == json ]]; then inputs=("$OUT"/obj/Release/net10.0/cir/*.cir.json)
    else inputs=("$OUT"/msgpack-cir/*.cir.msgpack); fi
    dotnet "$ILEMIT_DLL" "$OUT/codec-$format" CodecProbe \
        --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL")" \
        --runtime-refs "$STDLIB_RT_DLL" --target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" \
        --assembly-options "$OUT/obj/Release/net10.0/dotkt-resolved-emit-options.json" \
        "${inputs[@]}"
    cp "$STDLIB_RT_DLL" "$OUT/codec-$format/"
    write_runtimeconfig "$OUT/codec-$format" CodecProbe
    [[ "$(dotnet "$OUT/codec-$format/CodecProbe.dll")" == target-universe ]] || die "CIR $format runtime differs"
done
cmp "$OUT/codec-json/CodecProbe.dll" "$OUT/codec-msgpack/CodecProbe.dll" \
    || die "CIR codecs changed deterministic CIL/metadata"
echo "CIR JSON/MessagePack: identical deterministic assembly and runtime result"

# Changing transport invalidates only lowering/emission, including when switching back.
bir_stamp="$(stat -c '%y' "$OUT/obj/Release/net10.0/bir/.stamp")"
for format in MsgPack json; do
    dotnet build "$ROOT/tests/target-universe/TargetUniverseProbe.ktproj" \
        -c Release --no-restore -v:q --nologo -p:Deterministic=true -p:DotKtCirFormat="$format" \
        -p:BaseIntermediateOutputPath="$OUT/obj/" -p:BaseOutputPath="$OUT/bin/" >/dev/null
    [[ -f "$OUT/obj/Release/net10.0/cir/.stamp.${format,,}" ]] || die "CIR format stamp was not refreshed"
    [[ "$(stat -c '%y' "$OUT/obj/Release/net10.0/bir/.stamp")" == "$bir_stamp" ]] || die "CIR codec change recompiled Kotlin"
    [[ "$(dotnet "$probe_dir/TargetUniverseProbe.dll")" == target-universe ]] || die "SDK CIR $format runtime differs"
done

# BIR transport is a frontend output choice. Switching it recompiles Kotlin,
# replaces the old format cohort and retains identical deterministic output.
for format in MsgPack json; do
    prior_bir_stamp="$(stat -c '%y' "$OUT/obj/Release/net10.0/bir/.stamp")"
    dotnet build "$ROOT/tests/target-universe/TargetUniverseProbe.ktproj" \
        -c Release --no-restore -v:q --nologo -p:Deterministic=true -p:DotKtBirFormat="$format" -p:DotKtCirFormat="$format" \
        -p:BaseIntermediateOutputPath="$OUT/obj/" -p:BaseOutputPath="$OUT/bin/" >/dev/null
    [[ "$(stat -c '%y' "$OUT/obj/Release/net10.0/bir/.stamp")" != "$prior_bir_stamp" ]] || die "BIR codec change did not recompile Kotlin"
    [[ -f "$OUT/obj/Release/net10.0/bir/TargetUniverseProbe.bir.${format,,}" ]] || die "SDK did not select the BIR writer"
    if [[ "${format,,}" == msgpack ]]; then old_format=json; else old_format=msgpack; fi
    [[ -z "$(find "$OUT/obj/Release/net10.0/bir" -maxdepth 1 -name "*.bir.$old_format" -print -quit)" ]] || die "Stale BIR format survived recompilation"
    [[ "$(dotnet "$probe_dir/TargetUniverseProbe.dll")" == target-universe ]] || die "SDK BIR $format runtime differs"
    cmp "$raw" "$probe_dir/TargetUniverseProbe.dll" || die "BIR codecs changed deterministic CIL/metadata"
    if [[ "${format,,}" == msgpack ]]; then
        stable_bir_stamp="$(stat -c '%y' "$OUT/obj/Release/net10.0/bir/.stamp")"
        stable_cir_stamp="$(stat -c '%y' "$OUT/obj/Release/net10.0/cir/.stamp.msgpack")"
        dotnet build "$ROOT/tests/target-universe/TargetUniverseProbe.ktproj" \
            -c Release --no-restore -v:q --nologo -p:Deterministic=true -p:DotKtBirFormat=msgpack -p:DotKtCirFormat=msgpack \
            -p:BaseIntermediateOutputPath="$OUT/obj/" -p:BaseOutputPath="$OUT/bin/" >/dev/null
        [[ "$(stat -c '%y' "$OUT/obj/Release/net10.0/bir/.stamp")" == "$stable_bir_stamp" ]] || die "Unchanged binary BIR recompiled Kotlin"
        [[ "$(stat -c '%y' "$OUT/obj/Release/net10.0/cir/.stamp.msgpack")" == "$stable_cir_stamp" ]] || die "Unchanged binary CIR reran lowering"
    fi
done

dotnet run --project "$ROOT/tests/target-universe/MetadataProbe.csproj" -c Release -- \
    "$raw" "$ILEMIT_DLL"

# Preserve all physical generic-constraint rows, independently of Kotlin lowering choices.
cir_sources=("$OUT"/obj/Release/net10.0/cir/*.cir.json)
constraint_project="$ROOT/tests/constraint-emission/ConstraintProbe.csproj"
dotnet run --project "$constraint_project" -c Release -- generate \
    "${cir_sources[0]}" "$OUT/constraints.cir.json"
dotnet "$ILEMIT_DLL" "$OUT/constraints" ConstraintEmission --compile-refs "$FRAMEWORK_COMPILE_REFS" \
    "$OUT/constraints.cir.json"
dotnet run --project "$constraint_project" -c Release -- verify "$OUT/constraints/ConstraintEmission.dll"
bash "$ROOT/tests/run-ilverify.sh" "$OUT/constraints/ConstraintEmission.dll"

# Signature identity includes custom modifiers even when runtime parameter types coincide.
signature_project="$ROOT/tests/signature-emission/SignatureProbe.csproj"
dotnet run --project "$signature_project" -c Release -- generate \
    "${cir_sources[0]}" "$OUT/signatures.cir.json"
dotnet "$ILEMIT_DLL" "$OUT/signatures" SignatureEmission --compile-refs "$FRAMEWORK_COMPILE_REFS" \
    "$OUT/signatures.cir.json"
dotnet run --project "$signature_project" -c Release -- verify "$OUT/signatures/SignatureEmission.dll"
bash "$ROOT/tests/run-ilverify.sh" "$OUT/signatures/SignatureEmission.dll"
