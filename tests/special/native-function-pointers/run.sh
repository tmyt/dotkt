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
printf '%s\n' "$reference" > "$OUT/references.rsp"
dotnet "$ROOT/build/dll2klib-bin/dll2klib.dll" --out "$OUT/klib" @"$OUT/references.rsp"
separator=:
[[ "${OS:-}" != Windows_NT ]] || separator=';'
"$KOTC" "$ROOT/tests/special/native-function-pointers/consumer.kt" -no-stdlib \
    -classpath "$FE_KLIB$separator$OUT/klib/NativePointers.klib" -d "$OUT/bir"
dotnet "$BIR2CIR_DLL" "$OUT/cir" --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$reference")" \
    "$OUT/bir/consumer.bir.json"
dotnet "$ILEMIT_DLL" "$OUT/il" NativePointerConsumer \
    --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL" "$reference")" \
    --runtime-refs "$(refset_join "$STDLIB_RT_DLL" "$implementation")" \
    "$OUT/cir/consumer.cir.json"
write_runtimeconfig "$OUT/il" NativePointerConsumer
cp "$STDLIB_RT_DLL" "$implementation" "$OUT/il/"
actual="$(dotnet "$OUT/il/NativePointerConsumer.dll")"
[[ "$actual" == 84 ]] || die "native function-pointer consumer returned '$actual', expected 84"
echo "Native function-pointer DLL/KLIB/BIR/CIR/CIL execution: PASS"
dotnet run --project "$ROOT/tests/special/native-function-pointers/consumer/Consumer.csproj" -c Release \
    -p:ConsumerAssembly="$OUT/il/NativePointerConsumer.dll"
