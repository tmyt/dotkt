#!/usr/bin/env bash
# Explicit frontend friendship admits an internal override of a separately emitted published slot. Ordinary
# roundtrip consumers and visibility-negative probes remain non-friends; no CLR friend-assembly wiring is added.
source "$(cd -- "$(dirname -- "$0")/../.." && pwd -P)/scripts/lib.sh"
[[ $# == 3 ]] || die "expected consumer KLIB directory, producer DLL and metadata inspector DLL"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
classpath="$FE_KLIB"
case "${OS:-}" in Windows_NT) separator=';' ;; *) separator=':' ;; esac
while IFS= read -r klib; do classpath+="$separator$klib"; done \
    < <(find "$1" -maxdepth 1 -type f -name '*.klib' | LC_ALL=C sort)
"$KOTC" "$ROOT/tests/roundtrip/reference-override/PublishedOverrideFriend.kt" -no-stdlib \
    -classpath "$classpath" "-Xfriend-paths=$1/RoundtripProducer.klib" -d "$work/bir"
need_dotnet_reference_sets
# The producer's one native project dependency is part of this fixture's explicit reference catalog.
dependency="$(dirname "$2")/RoundtripPropertySlot.dll"
runtime_refs="$(refset_join "$STDLIB_RT_DLL" "$2" "$dependency")"
compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$runtime_refs")"
dotnet "$BIR2CIR_DLL" "$work/cir" --compile-refs "$(refset_join "$compile_refs" "$STDLIB_REF_DLL")" \
    "$work/bir/PublishedOverrideFriend.bir.json"
dotnet "$ILEMIT_DLL" "$work/dll" PublishedOverrideFriend --compile-refs "$compile_refs" \
    --runtime-refs "$runtime_refs" --target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" \
    "$work/cir/"*.cir.json
dotnet "$3" --referenced-override-accessibility "$work/dll/PublishedOverrideFriend.dll" \
    "$work/bir/PublishedOverrideFriend.bir.json" "$work/cir/PublishedOverrideFriend.cir.json"
cp "$STDLIB_RT_DLL" "$2" "$dependency" "$work/dll/"
write_runtimeconfig "$work/dll" PublishedOverrideFriend
dotnet "$work/dll/PublishedOverrideFriend.dll"
bash "$ROOT/tests/run-ilverify.sh" "$work/dll/PublishedOverrideFriend.dll"
