#!/usr/bin/env bash
# Source -> implementation/reference DLL -> fresh KLIB -> Kotlin consumer.
source "$(cd "$(dirname "$0")/../../.." && pwd)/scripts/lib.sh"
HERE="$ROOT/tests/special/data-class-identity"
OUT="$ROOT/build/data-class-identity"
mkdir -p "$OUT/tools" "$OUT/native"
need_kotc
build_tool dll2klib
build_tool bir2cir
build_tool ilemit
need_fe_klib
need_stdlib_ref
need_stdlib_rt
need_dotnet_reference_sets
dotnet build "$ROOT/tests/roundtrip/metadata-inspector/CompanionMetadataInspector.csproj" \
    -c Release -o "$OUT/tools" --nologo -v:q
dotnet build "$HERE/Native.csproj" -c Release -o "$OUT/native" --nologo -v:q
inspector="$OUT/tools/CompanionMetadataInspector.dll"
case "${OS:-}" in Windows_NT) cp_sep=';' ;; *) cp_sep=':' ;; esac
lowering_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$STDLIB_RT_DLL")"
emission_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL")"
for codec in json msgpack; do
    run="$OUT/$codec"
    mkdir -p "$run/bir" "$run/cir" "$run/il" "$run/ref" "$run/runtime-cir" "$run/runtime-il"
    "$KOTC" "$HERE/producer.kt" -no-stdlib -classpath "$FE_KLIB" \
        -P "plugin:dotkt:bir-format=$codec" -d "$run/bir"
    dotnet "$BIR2CIR_DLL" "$run/cir" --compile-refs "$lowering_refs" --cir-format="$codec" \
        "$run/bir/producer.bir.$codec"
    dotnet "$ILEMIT_DLL" "$run/il" DataIdentity --compile-refs "$emission_refs" \
        --runtime-refs "$STDLIB_RT_DLL" --reference-assembly "$run/ref/DataIdentity.dll" \
        "$run/cir/"*.cir."$codec"
    # The stdlib runtime mode also retains source data identity when stripping
    # its other compile-time type metadata.
    dotnet "$BIR2CIR_DLL" "$run/runtime-cir" --build-stdlib=runtime --compile-refs "$lowering_refs" \
        --cir-format="$codec" "$run/bir/producer.bir.$codec"
    dotnet "$ILEMIT_DLL" "$run/runtime-il" DataIdentityRuntime --build-stdlib=runtime \
        --compile-refs "$emission_refs" --runtime-refs "$STDLIB_RT_DLL" "$run/runtime-cir/"*.cir."$codec"
    # Marked stdlib assemblies are deliberately excluded from KLIB projection;
    # inspect this runtime-mode artifact directly and project both library views.
    dotnet "$inspector" --data-markers-only "$run/runtime-il/DataIdentityRuntime.dll"
    for view in il ref; do
        producer="$run/$view/DataIdentity.dll"
        printf '%s\n' "${FRAMEWORK_COMPILE_REF_PATHS[@]}" "$STDLIB_RT_DLL" "$producer" \
            "$OUT/native/Native.dll" > "$run/$view/references.rsp"
        dotnet "$DLL2KLIB_DLL" --out "$run/$view/klib" --jobs 0 @"$run/$view/references.rsp" \
            > "$run/$view/projection.log"
        producer_klib="$run/$view/klib/$(basename "$producer" .dll).klib"
        dotnet "$inspector" --data-identity "$producer" "$producer_klib"
        dotnet "$inspector" --native-data-identity "$OUT/native/Native.dll" "$run/$view/klib/Native.klib"
        mkdir -p "$run/$view/consumer-bir" "$run/$view/consumer-cir" "$run/$view/consumer-il"
        "$KOTC" "$HERE/consumer.kt" -no-stdlib -classpath "$FE_KLIB$cp_sep$producer_klib" \
            -P "plugin:dotkt:bir-format=$codec" -d "$run/$view/consumer-bir"
        dotnet "$BIR2CIR_DLL" "$run/$view/consumer-cir" --cir-format="$codec" \
            --compile-refs "$(refset_join "$lowering_refs" "$producer")" "$run/$view/consumer-bir/consumer.bir.$codec"
        dotnet "$ILEMIT_DLL" "$run/$view/consumer-il" DataIdentityConsumer \
            --compile-refs "$(refset_join "$emission_refs" "$producer")" \
            --runtime-refs "$(refset_join "$STDLIB_RT_DLL" "$run/il/DataIdentity.dll")" \
            --target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" "$run/$view/consumer-cir/"*.cir."$codec"
        write_runtimeconfig "$run/$view/consumer-il" DataIdentityConsumer
        dotnet "$inspector" --data-consumer-accessors "$run/$view/consumer-il/DataIdentityConsumer.dll"
        cp "$STDLIB_RT_DLL" "$run/il/DataIdentity.dll" "$run/$view/consumer-il/"
        actual="$(dotnet "$run/$view/consumer-il/DataIdentityConsumer.dll")"
        [[ "$actual" == 'data identity roundtrip: OK' ]] || die "data identity consumer returned '$actual'"
        bash "$ROOT/tests/run-ilverify.sh" "$run/$view/consumer-il/DataIdentityConsumer.dll"
    done
    cp "$STDLIB_RT_DLL" "$run/il/"
    cp "$STDLIB_RT_DLL" "$run/runtime-il/"
    bash "$ROOT/tests/run-ilverify.sh" "$run/il/DataIdentity.dll" "$run/runtime-il/DataIdentityRuntime.dll"
done
info 'PASS data identity (JSON/MessagePack, implementation/reference KLIBs, runtime markers, native controls and consumers)'
