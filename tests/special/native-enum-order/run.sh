#!/usr/bin/env bash
source "$(cd "$(dirname "$0")/../../.." && pwd)/scripts/lib.sh"
HERE="$ROOT/tests/special/native-enum-order"
OUT="$ROOT/build/native-enum-order"
mkdir -p "$OUT/tools" "$OUT/fixture" "$OUT/klib" "$OUT/bir" "$OUT/cir" "$OUT/il"
need_kotc
need_fe_klib
need_tool dll2klib
need_tool bir2cir
need_tool ilemit
need_stdlib_ref
need_stdlib_rt
need_dotnet_reference_sets
dotnet build "$HERE/fixture/Fixture.csproj" -c Release -o "$OUT/tools" --nologo -v:q
dotnet "$OUT/tools/MetadataOrder.dll" "$OUT/fixture/MetadataOrder.dll"
dotnet build "$HERE/char-fixture/CharEnums.fsproj" -c Release -o "$OUT/fixture" --nologo -v:q
printf '%s\n' "${FRAMEWORK_COMPILE_REF_PATHS[@]}" "$OUT/fixture/MetadataOrder.dll" \
    "$OUT/fixture/CharEnums.dll" "$OUT/fixture/FSharp.Core.dll" > "$OUT/references.rsp"
dotnet "$DLL2KLIB_DLL" --out "$OUT/klib" --jobs 0 @"$OUT/references.rsp" > "$OUT/projection.log"
case "${OS:-}" in Windows_NT) cp_sep=';' ;; *) cp_sep=':' ;; esac
classpath="$FE_KLIB"
while IFS= read -r klib; do classpath+="$cp_sep$klib"; done < <(find "$OUT/klib" -maxdepth 1 -name '*.klib' | LC_ALL=C sort)
"$KOTC" "$HERE/consumer.kt" -no-stdlib -classpath "$classpath" -d "$OUT/bir"
fixtures="$(refset_join "$OUT/fixture/MetadataOrder.dll" "$OUT/fixture/CharEnums.dll" "$OUT/fixture/FSharp.Core.dll")"
dotnet "$BIR2CIR_DLL" "$OUT/cir" --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$fixtures")" "$OUT/bir/consumer.bir.json"
dotnet "$ILEMIT_DLL" "$OUT/il" NativeEnumOrder \
    --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL" "$fixtures")" \
    --runtime-refs "$(refset_join "$STDLIB_RT_DLL" "$fixtures")" \
    --target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" "$OUT/cir/consumer.cir.json"
write_runtimeconfig "$OUT/il" NativeEnumOrder
cp "$STDLIB_RT_DLL" "$OUT/fixture/MetadataOrder.dll" "$OUT/fixture/CharEnums.dll" "$OUT/fixture/FSharp.Core.dll" "$OUT/il/"
bash "$ROOT/tests/run-ilverify.sh" "$OUT/fixture/MetadataOrder.dll" "$OUT/fixture/CharEnums.dll" "$OUT/il/NativeEnumOrder.dll"
actual="$(dotnet "$OUT/il/NativeEnumOrder.dll")"
[[ "$actual" == 'native enum metadata order: OK' ]] || die "native enum projection/order returned '$actual'"
info 'PASS  native enum metadata order (projected fields, property/function references, unknown bits, char, generic values)'
