#!/usr/bin/env bash
# Feed actual frontend output through the metadata body's stripping path, emit it without the stdlib-only
# assembly marker, then project the saved PE. No implementation bodies are consulted during projection.
source "$(cd -- "$(dirname -- "$0")/../.." && pwd -P)/scripts/lib.sh"
[[ $# == 2 ]] || die "expected producer BIR and metadata inspector DLL"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
need_dotnet_reference_sets
refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL")"
dotnet "$BIR2CIR_DLL" "$work/cir" --compile-refs "$refs" --build-stdlib=metadata \
    "$1"
dotnet "$ILEMIT_DLL" "$work/dll" CallableVisibilityReference --compile-refs "$refs" "$work/cir/"*.cir.json
printf '%s\n' "$work/dll/CallableVisibilityReference.dll" "$STDLIB_REF_DLL" \
    "${FRAMEWORK_COMPILE_REF_PATHS[@]}" > "$work/references.rsp"
dotnet "$DLL2KLIB_DLL" --out "$work/klib" --jobs 2 @"$work/references.rsp"
dotnet "$2" --reference-callable-visibility \
    "$work/dll/CallableVisibilityReference.dll" "$work/klib/CallableVisibilityReference.klib"
