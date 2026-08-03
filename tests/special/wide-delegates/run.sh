#!/usr/bin/env bash
# Regression gate for function types wider than System.Func/Action supports: System.Func tops out at 16
# value parameters plus TResult (Func`17); Kotlin function values can be wider, so ilemit synthesizes
# module-local delegate types DotKt.Runtime.CompilerServices.KFunc`N / KAction`N when needed. Drives
# tests/special/wide-delegates/wide.kt (17-arg function values) through the REAL pipeline — kotc -> bir2cir ->
# ilemit, the same single path every other gate uses. Runs the app, checks the synthesized delegate types
# exist in the dll, and that dll2klib restores the wide type as a Kotlin function type
# consumable from a second module. Exits nonzero on any failure.
ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
SCRIPT_NAME=wide-delegate-tests
source "$ROOT/scripts/lib.sh"

usage() { cat <<EOF
usage: $SCRIPT_NAME
Runs the >16-arg delegate synthesis regression check (no flags). -h for this help.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

OUT="$ROOT/build/wide-delegates"
rm -rf "$OUT"; mkdir -p "$OUT/bir" "$OUT/cir" "$OUT/il" "$OUT/consumer-bir" "$OUT/consumer-cir" "$OUT/consumer-il" "$OUT/reference-klibs"

# Unconditional tool builds: the gate tests the CURRENT sources. Stdlib artifact roles mirror verify-tests:
# the frontend KLIB is kotc's -classpath (kotlin.* comes from the klib, never dll2klib), the REFERENCE
# dll feeds bir2cir's @Clr labels, the RUNTIME dll backs println at run time.
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1
build_tool ilemit; build_tool bir2cir; build_tool dll2klib
need_fe_klib; need_stdlib_ref; need_stdlib_rt
need_dotnet_reference_sets

"$KOTC" "$ROOT/tests/special/wide-delegates" -no-stdlib -classpath "$FE_KLIB" -d "$OUT/bir" >/dev/null 2>&1 \
	|| die "kotc failed on tests/special/wide-delegates"
dotnet "$BIR2CIR_DLL" "$OUT/cir" --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL")" "$OUT/bir"/*.bir.json >/dev/null 2>&1 \
	|| die "bir2cir failed"
dotnet "$ILEMIT_DLL" "$OUT/il" Wide \
	--compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL")" \
	--runtime-refs "$STDLIB_RT_DLL" --target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" \
	"$OUT/cir"/*.cir.json >/dev/null 2>&1 \
	|| die "ilemit failed"
write_runtimeconfig "$OUT/il" Wide
cp "$STDLIB_RT_DLL" "$OUT/il/"

expected="$(printf '17\n17\n17\n23\n29\n31')"
if ! actual="$(dotnet "$OUT/il/Wide.dll" 2>/dev/null)"; then actual+="${actual:+$'\n'}(app crashed: exit $?)"; fi
if [[ "$actual" != "$expected" ]]; then
	echo "FAIL  wide delegate invocation" >&2
	printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$expected" "$actual" >&2
	exit 1
fi

# The wide shapes must have forced the module-local synthesized delegate types (not Func/Action).
if ! strings "$OUT/il/Wide.dll" | grep -q 'KFunc`18'; then
	echo "FAIL  emitted assembly is missing KFunc\`18" >&2
	exit 1
fi
if ! strings "$OUT/il/Wide.dll" | grep -q 'KAction`17'; then
	echo "FAIL  emitted assembly is missing KAction\`17" >&2
	exit 1
fi
# A TypeBuilder/composite-open generic in a low-arity signature must not silently change the ABI to a
# module-local KFunc/KAction. Only the genuinely wide families above may be synthesized.
for arity in $(seq 1 17); do
	if strings "$OUT/il/Wide.dll" | grep -qx "KFunc\`$arity"; then
		echo "FAIL  low-arity KFunc\`$arity was synthesized" >&2
		exit 1
	fi
done
for arity in $(seq 1 16); do
	if strings "$OUT/il/Wide.dll" | grep -qx "KAction\`$arity"; then
		echo "FAIL  low-arity KAction\`$arity was synthesized" >&2
		exit 1
	fi
done

# Round-trip surface: dll2klib must restore the KFunc`18-typed parameter as a Kotlin function type.
# Compile and run a second module whose 17-argument lambda can only bind if the standard KLIB metadata
# carries all 17 Int parameters and the Int return.
compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL")"
printf '%s\n' "${FRAMEWORK_COMPILE_REF_PATHS[@]}" > "$OUT/references.rsp"
dotnet "$DLL2KLIB_DLL" --out "$OUT/reference-klibs" \
	--jobs "${DOTKT_DLL2KLIB_JOBS:-$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf '1')}" \
	@"$OUT/references.rsp" >/dev/null
dotnet "$DLL2KLIB_DLL" "$OUT/il/Wide.dll" "$OUT/Wide.klib" >/dev/null
case "${OS:-}" in Windows_NT) cp_sep=';' ;; *) cp_sep=':' ;; esac
consumer_cp="$FE_KLIB"
while IFS= read -r klib; do consumer_cp+="$cp_sep$klib"; done \
	< <(find "$OUT/reference-klibs" -maxdepth 1 -type f -name '*.klib' | LC_ALL=C sort)
consumer_cp+="$cp_sep$OUT/Wide.klib"
cat > "$OUT/consumer.kt" <<'EOF'
fun main() {
    println(accept { p1, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, p17 -> p1 + p17 })
}
EOF
"$KOTC" "$OUT/consumer.kt" -no-stdlib -classpath "$consumer_cp" -d "$OUT/consumer-bir" >/dev/null 2>&1 \
	|| die "kotc did not restore KFunc\`18 as a 17-argument Kotlin function type"
dotnet "$BIR2CIR_DLL" "$OUT/consumer-cir" \
	--compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$OUT/il/Wide.dll")" \
	"$OUT/consumer-bir"/*.bir.json >/dev/null 2>&1 || die "consumer bir2cir failed"
dotnet "$ILEMIT_DLL" "$OUT/consumer-il" WideConsumer \
	--compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL" "$OUT/il/Wide.dll")" \
	--runtime-refs "$(refset_join "$STDLIB_RT_DLL" "$OUT/il/Wide.dll")" \
	--target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" \
	"$OUT/consumer-cir"/*.cir.json >/dev/null 2>&1 || die "consumer ilemit failed"
write_runtimeconfig "$OUT/consumer-il" WideConsumer
cp "$STDLIB_RT_DLL" "$OUT/il/Wide.dll" "$OUT/consumer-il/"
consumer_actual="$(dotnet "$OUT/consumer-il/WideConsumer.dll" 2>/dev/null)" \
	|| die "wide-delegate consumer failed at runtime"
[[ "$consumer_actual" == 18 ]] || die "wide-delegate consumer returned '$consumer_actual', expected 18"

info "PASS  wide synthetic delegates (kotc -> bir2cir -> ilemit; run + KFunc\`18/KAction\`17 + dll2klib/KLIB re-consumption)"
