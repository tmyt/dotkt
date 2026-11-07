#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SCRIPT_NAME=native-function-pointer-diagnostics
source "$ROOT/scripts/lib.sh"
OUT="$ROOT/build/tests-native-function-pointers/diagnostics"
mkdir -p "$OUT"
need_dotnet_reference_sets
dotnet run --project "$ROOT/tests/special/native-function-pointers/diagnostics/Diagnostics.csproj" -c Release -- \
    "$OUT/input" "$DOTNET_REFPACK_DIR/System.Runtime.dll"
for name in NativeVararg NativeGeneric; do
    printf '%s\n' "$OUT/input/$name.dll" > "$OUT/$name.rsp"
    if dotnet "$DLL2KLIB_DLL" --out "$OUT/$name-klib" @"$OUT/$name.rsp" > "$OUT/$name.log" 2>&1; then
        die "$name was projected without its unsupported-convention diagnostic"
    fi
    grep -q 'Unsupported generic/vararg/instance CLR function-pointer calling convention' "$OUT/$name.log" \
        || die "$name failed outside the supported-signature diagnostic boundary"
done
printf '%s\n' "$OUT/input/NativeModifiers.dll" > "$OUT/modifiers.rsp"
dotnet "$DLL2KLIB_DLL" --out "$OUT/modifiers-klib" @"$OUT/modifiers.rsp"
separator=:
[[ "${OS:-}" != Windows_NT ]] || separator=';'
"$KOTC" "$ROOT/tests/special/native-function-pointers/unsupported.kt" -no-stdlib \
    -classpath "$FE_KLIB$separator$OUT/modifiers-klib/NativeModifiers.klib" -d "$OUT/bir"
if dotnet "$BIR2CIR_DLL" "$OUT/cir" \
    --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$OUT/input/NativeModifiers.dll")" \
    "$OUT/bir/unsupported.bir.json" > "$OUT/modifiers.log" 2>&1; then
    die "Unrepresentable custom-modifier order reached CIR without a diagnostic"
fi
grep -q 'required/optional native modifier order is not supported' "$OUT/modifiers.log" \
    || die "Native modifier order failed outside its representability diagnostic boundary"
"$KOTC" "$ROOT/tests/special/native-function-pointers/unsupported-box.kt" -no-stdlib \
    -classpath "$FE_KLIB$separator$ROOT/build/tests-native-function-pointers/klib/NativePointers.klib" -d "$OUT/box-bir"
if dotnet "$BIR2CIR_DLL" "$OUT/box-cir" \
    --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$ROOT/tests/special/native-function-pointers/reference/obj/Release/net10.0/ref/NativePointers.dll")" \
    "$OUT/box-bir/unsupported-box.bir.json" > "$OUT/box.log" 2>&1; then
    die "A native function pointer was admitted to object storage"
fi
grep -q 'native function-pointer value requires its exact signature' "$OUT/box.log" \
    || die "Native boxing failed outside its representation diagnostic boundary"
echo "Unsupported current native signatures: PASS"
