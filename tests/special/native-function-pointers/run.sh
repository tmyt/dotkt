#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SCRIPT_NAME=native-function-pointers
source "$ROOT/scripts/lib.sh"
OUT="$ROOT/build/tests-native-function-pointers"
mkdir -p "$OUT"
need_kotc
build_tool bir2cir
build_tool ilemit
build_tool dll2klib
need_fe_klib
need_stdlib_ref
need_stdlib_rt
need_dotnet_reference_sets
dotnet build "$ROOT/tests/special/native-function-pointers/reference/NativePointers.csproj" -c Release -v:q --nologo
reference="$ROOT/tests/special/native-function-pointers/reference/obj/Release/net10.0/ref/NativePointers.dll"
implementation="$ROOT/tests/special/native-function-pointers/reference/bin/Release/net10.0/NativePointers.dll"
printf '%s\n' "$reference" "$DOTNET_REFPACK_DIR/System.Runtime.dll" "$STDLIB_RT_DLL" > "$OUT/references.rsp"
dotnet "$ROOT/build/dll2klib-bin/dll2klib.dll" --out "$OUT/klib" @"$OUT/references.rsp"
separator=:
[[ "${OS:-}" != Windows_NT ]] || separator=';'
"$KOTC" "$ROOT/tests/special/native-function-pointers/consumer.kt" -no-stdlib \
    -classpath "$FE_KLIB$separator$OUT/klib/NativePointers.klib$separator$OUT/klib/System.Runtime.klib" -d "$OUT/bir"
dotnet "$BIR2CIR_DLL" "$OUT/cir" --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$reference")" \
    "$OUT/bir/consumer.bir.json"
dotnet "$ILEMIT_DLL" "$OUT/il" NativePointerConsumer \
    --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL" "$reference")" \
    --runtime-refs "$(refset_join "$STDLIB_RT_DLL" "$implementation")" \
    "$OUT/cir"/*.cir.json
write_runtimeconfig "$OUT/il" NativePointerConsumer
cp "$STDLIB_RT_DLL" "$implementation" "$OUT/il/"
actual="$(dotnet "$OUT/il/NativePointerConsumer.dll")"
[[ "$actual" == 252 ]] || die "native function-pointer consumer returned '$actual', expected 252"
echo "Native function-pointer DLL/KLIB/BIR/CIR/CIL execution: PASS"
dotnet run --project "$ROOT/tests/special/native-function-pointers/consumer/Consumer.csproj" -c Release \
    -p:ConsumerAssembly="$OUT/il/NativePointerConsumer.dll"
dotnet run --project "$ROOT/tests/special/native-function-pointers/metadata/Metadata.csproj" -c Release -- \
    "$reference" "$OUT/il/NativePointerConsumer.dll"
bash "$ROOT/tests/special/native-function-pointers/run-diagnostics.sh"

# Reconsume the Kotlin declaration metadata as well as the original C# API.
printf '%s\n' "$reference" "$DOTNET_REFPACK_DIR/System.Runtime.dll" "$STDLIB_RT_DLL" \
    "$OUT/il/NativePointerConsumer.dll" > "$OUT/roundtrip.rsp"
dotnet "$DLL2KLIB_DLL" --out "$OUT/roundtrip-klib" @"$OUT/roundtrip.rsp"
"$KOTC" "$ROOT/tests/special/native-function-pointers/roundtrip.kt" -no-stdlib \
    -classpath "$FE_KLIB$separator$OUT/roundtrip-klib/NativePointers.klib$separator$OUT/roundtrip-klib/System.Runtime.klib$separator$OUT/roundtrip-klib/NativePointerConsumer.klib" \
    -d "$OUT/roundtrip-bir"
dotnet "$BIR2CIR_DLL" "$OUT/roundtrip-cir" \
    --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$reference" "$OUT/il/NativePointerConsumer.dll")" \
    "$OUT/roundtrip-bir/roundtrip.bir.json"
dotnet "$ILEMIT_DLL" "$OUT/roundtrip-il" NativePointerRoundtrip \
    --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL" "$reference" "$OUT/il/NativePointerConsumer.dll")" \
    --runtime-refs "$(refset_join "$STDLIB_RT_DLL" "$implementation" "$OUT/il/NativePointerConsumer.dll")" \
    "$OUT/roundtrip-cir"/*.cir.json
write_runtimeconfig "$OUT/roundtrip-il" NativePointerRoundtrip
cp "$STDLIB_RT_DLL" "$implementation" "$OUT/il/NativePointerConsumer.dll" "$OUT/roundtrip-il/"
roundtrip_actual="$(dotnet "$OUT/roundtrip-il/NativePointerRoundtrip.dll")"
[[ "$roundtrip_actual" == 84 ]] || die "native function-pointer cross-module consumer returned '$roundtrip_actual', expected 84"
echo "Native function-pointer Kotlin declaration roundtrip: PASS"
