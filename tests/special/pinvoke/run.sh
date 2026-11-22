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
	find "$CACHE" -mindepth 1 -maxdepth 1 -type d -iname 'dotkt.*' -exec rm -rf -- {} +
fi

# Exec must report the compiler error at quiet verbosity even when its source
# location is longer than MSBuild's diagnostic-header scan.
diagnostic_dir="$OUT/diagnostic"
while (( ${#diagnostic_dir} < 400 )); do diagnostic_dir+="/long-source-directory"; done
mkdir -p "$diagnostic_dir"
diagnostic_source="$diagnostic_dir/UnsupportedCallback.kt"
cp "$ROOT/tests/special/pinvoke/UnsupportedCallback.kt" "$diagnostic_source"
if dotnet build "$ROOT/tests/special/pinvoke/diagnostic/PInvokeDiagnostic.ktproj" \
    -p:DiagnosticSource="$diagnostic_source" --no-incremental -v:q --nologo >"$OUT/diagnostic.log" 2>&1; then
    die "unsupported P/Invoke unexpectedly compiled"
fi
grep -F "error DOTKTPINVOKE001: P/Invoke 'unsupportedCallback' does not support parameter 'callback' of type 'fnptr(System.Int32[,]) -> System.Int32'" "$OUT/diagnostic.log" | \
    grep -F "$diagnostic_source(7," >/dev/null || { cat "$OUT/diagnostic.log"; die "quiet build lost compiler diagnostic or source"; }

cc -shared -fPIC "$ROOT/tests/special/pinvoke/native/probe.c" \
	-o "$OUT/native/libdotkt_pinvoke_probe.so"
dotnet build "$ROOT/tests/special/pinvoke/inspector/PInvokeInspector.csproj" \
	-c Release -o "$OUT/inspector" -v:q --nologo
dotnet build "$ROOT/tests/special/pinvoke/consumer/PInvokeConsumer.ktproj" \
	-c Release --no-incremental -m:1 -v:q --nologo
dotnet build "$ROOT/tests/special/pinvoke/csharp-consumer/PInvokeCSharpConsumer.csproj" \
	-c Release --no-incremental -m:1 -v:q --nologo
dotnet build "$ROOT/tests/special/pinvoke/kotlin-consumes-csharp/PInvokeKotlinConsumesCSharp.ktproj" \
	-c Release --no-incremental -m:1 -v:q --nologo

PRODUCER_DLL="$ROOT/tests/special/pinvoke/producer/bin/Release/net10.0/PInvokeProducer.dll"
PRODUCER_REF="$ROOT/tests/special/pinvoke/producer/obj/Release/net10.0/ref/PInvokeProducer.dll"
PRODUCER_KLIB="$ROOT/tests/special/pinvoke/consumer/obj/Release/net10.0/klib/PInvokeProducer.klib"
PRODUCER_KLIB_RSP="$ROOT/tests/special/pinvoke/consumer/obj/Release/net10.0/dotkt-reference-klibs.rsp"
CONSUMER_DLL="$ROOT/tests/special/pinvoke/consumer/bin/Release/net10.0/PInvokeConsumer.dll"
CSHARP_CONSUMER_DLL="$ROOT/tests/special/pinvoke/csharp-consumer/bin/Release/net10.0/PInvokeCSharpConsumer.dll"
CSHARP_PRODUCER_KLIB="$ROOT/tests/special/pinvoke/kotlin-consumes-csharp/obj/Release/net10.0/klib/PInvokeCSharpProducer.klib"
CSHARP_PRODUCER_REF="$ROOT/tests/special/pinvoke/csharp-producer/obj/Release/net10.0/ref/PInvokeCSharpProducer.dll"
CSHARP_KLIB_RSP="$ROOT/tests/special/pinvoke/kotlin-consumes-csharp/obj/Release/net10.0/dotkt-reference-klibs.rsp"
KOTLIN_CSHARP_CONSUMER_DLL="$ROOT/tests/special/pinvoke/kotlin-consumes-csharp/bin/Release/net10.0/PInvokeKotlinConsumesCSharp.dll"
INSPECTOR="$OUT/inspector/PInvokeInspector.dll"
for artifact in "$PRODUCER_DLL" "$PRODUCER_REF" "$PRODUCER_KLIB" "$CONSUMER_DLL" "$CSHARP_CONSUMER_DLL" \
	"$CSHARP_PRODUCER_KLIB" "$KOTLIN_CSHARP_CONSUMER_DLL" "$INSPECTOR"; do
	[[ -f "$artifact" ]] || die "missing expected artifact $artifact"
done
grep -Fx "$PRODUCER_REF" "$PRODUCER_KLIB_RSP" >/dev/null ||
	die "dll2klib did not consume the Kotlin P/Invoke reference assembly"
[[ -f "$CSHARP_PRODUCER_REF" ]] || die "missing C# P/Invoke reference assembly"
grep -Fx "$CSHARP_PRODUCER_REF" "$CSHARP_KLIB_RSP" >/dev/null ||
	die "dll2klib did not consume the C# P/Invoke reference assembly"

dotnet "$INSPECTOR" "$PRODUCER_DLL" "$PRODUCER_KLIB" "$CSHARP_PRODUCER_KLIB" "$KOTLIN_CSHARP_CONSUMER_DLL" "$PRODUCER_REF"
LD_LIBRARY_PATH="$OUT/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" dotnet "$CONSUMER_DLL"
LD_LIBRARY_PATH="$OUT/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" dotnet "$CSHARP_CONSUMER_DLL"
LD_LIBRARY_PATH="$OUT/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" dotnet "$KOTLIN_CSHARP_CONSUMER_DLL"
bash "$ROOT/tests/run-ilverify.sh" "$PRODUCER_DLL" "$CONSUMER_DLL" "$KOTLIN_CSHARP_CONSUMER_DLL"

cir_dir="$ROOT/tests/special/pinvoke/producer/obj/Release/net10.0/cir"
jq -s -e '
  [.[] | .. | objects | select(.pinvoke != null and .name != null)] as $imports |
  ($imports | length) == 13 and
  all($imports[];
    .extern == true and (.body | length) == 0 and
    ((.mods.external // false) == false) and
    all(.attrs[]?; .attr.name != "System.Runtime.InteropServices.DllImportAttribute"))
' "$cir_dir"/*.cir.json >/dev/null || die "BIR external + DllImport facts did not become thirteen bodyless CIR pinvoke descriptors"

info "PASS  Kotlin/C# DllImport emits exact MethodImport metadata, executes, and round-trips through dll2klib"
