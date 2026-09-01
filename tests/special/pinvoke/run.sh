#!/usr/bin/env bash
# Kotlin DllImport -> CIR MethodImport -> runtime, plus reference-DLL -> dll2klib -> Kotlin consumer round-trip.
ROOT="$(cd "$(dirname "$0")/../../.." && pwd -P)"
SCRIPT_NAME=pinvoke
source "$ROOT/scripts/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME
Runs the CLR P/Invoke declaration and dll2klib round-trip regression. -h for this help.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

OUT="$ROOT/build/pinvoke"
CACHE="$ROOT/build/test-package-cache"
rm -rf "$OUT"
mkdir -p "$OUT/native" "$OUT/inspector"
if [[ -d "$CACHE" ]]; then
	find "$CACHE" -maxdepth 1 -type d -iname 'dotkt.*' -exec rm -rf {} + 2>/dev/null || true
fi

cc -shared -fPIC "$ROOT/tests/special/pinvoke/native/probe.c" \
	-o "$OUT/native/libdotkt_pinvoke_probe.so"
dotnet build "$ROOT/tests/special/pinvoke/inspector/PInvokeInspector.csproj" \
	-c Release -o "$OUT/inspector" -v:q --nologo
dotnet build "$ROOT/tests/special/pinvoke/consumer/PInvokeConsumer.ktproj" \
	-c Release --no-incremental -m:1 -v:q --nologo

PRODUCER_DLL="$ROOT/tests/special/pinvoke/producer/bin/Release/net10.0/PInvokeProducer.dll"
PRODUCER_KLIB="$ROOT/tests/special/pinvoke/consumer/obj/Release/net10.0/klib/PInvokeProducer.klib"
CONSUMER_DLL="$ROOT/tests/special/pinvoke/consumer/bin/Release/net10.0/PInvokeConsumer.dll"
INSPECTOR="$OUT/inspector/PInvokeInspector.dll"
for artifact in "$PRODUCER_DLL" "$PRODUCER_KLIB" "$CONSUMER_DLL" "$INSPECTOR"; do
	[[ -f "$artifact" ]] || die "missing expected artifact $artifact"
done

dotnet "$INSPECTOR" "$PRODUCER_DLL" "$PRODUCER_KLIB"
LD_LIBRARY_PATH="$OUT/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" dotnet "$CONSUMER_DLL"

cir="$ROOT/tests/special/pinvoke/producer/obj/Release/net10.0/cir/NativeMethods.cir.json"
jq -e '
  [.methods[] | select(.pinvoke != null)] as $imports |
  ($imports | length) == 6 and
  all($imports[];
    .extern == true and (.body | length) == 0 and
    ((.mods.external // false) == false) and
    all(.attrs[]?; .attr.name != "System.Runtime.InteropServices.DllImportAttribute"))
' "$cir" >/dev/null || die "BIR external + DllImport facts did not become six bodyless CIR pinvoke descriptors"

info "PASS  Kotlin DllImport emits exact MethodImport metadata, executes, and round-trips through dll2klib"
