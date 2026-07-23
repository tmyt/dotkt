#!/usr/bin/env bash
# Regression gate for function types wider than System.Func/Action supports: System.Func tops out at 16
# value parameters plus TResult (Func`17); Kotlin function values can be wider, so ilemit synthesizes
# module-local delegate types DotKt.Runtime.CompilerServices.KFunc`N / KAction`N when needed. Drives
# tests/special/wide-delegates/wide.kt (17-arg function values) through the REAL pipeline — kotc -> bir2cir ->
# ilemit, the same single path every other gate uses. Runs the app, checks the synthesized delegate types
# exist in the dll, and that
# facadegen restores the wide type as a Kotlin function type. Exits nonzero on any failure.
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
rm -rf "$OUT"; mkdir -p "$OUT/bir" "$OUT/cir" "$OUT/il"

# Unconditional tool builds: the gate tests the CURRENT sources. Stdlib artifact roles mirror verify-tests:
# the frontend KLIB is kotc's -classpath (kotlin.* comes from the klib, never facadegen), the REFERENCE
# dll feeds bir2cir's @Clr labels, the RUNTIME dll backs println at run time.
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1
build_tool ilemit; build_tool bir2cir; build_tool facadegen; build_tool retarget
need_fe_klib; need_stdlib_ref; need_stdlib_rt
need_dotnet_reference_sets

"$KOTC" "$ROOT/tests/special/wide-delegates" -no-stdlib -classpath "$FE_KLIB" -d "$OUT/bir" >/dev/null 2>&1 \
	|| die "kotc failed on tests/special/wide-delegates"
dotnet "$BIR2CIR_DLL" "$OUT/cir" --compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL")" "$OUT/bir"/*.bir.json >/dev/null 2>&1 \
	|| die "bir2cir failed"
dotnet "$ILEMIT_DLL" "$OUT/il" Wide --runtime-refs "$STDLIB_RT_DLL" "$OUT/cir"/*.cir.json >/dev/null 2>&1 \
	|| die "ilemit failed"
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

# Round-trip surface: facadegen must restore the KFunc`18-typed parameter as a Kotlin function type.
# facadegen reads the delegate's Invoke signature (17 params + Int return) directly, regardless of arity
# or the [CompilerGenerated] stamp, and emits it as the JSON function-type node `{"t":"fn",...}` in the
# FIR-injection meta. Assert `accept`'s `cb` param is that fn node with 17 Int params and an Int return.
compile_refs="$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL")"
dotnet "$RETARGET_DLL" "$OUT/il/Wide.dll" --compile-refs "$compile_refs" >/dev/null 2>&1 \
	|| die "retarget failed"
dotnet "$FACADEGEN_DLL" "$OUT/wide.meta" --compile-refs "$(refset_join "$compile_refs" "$OUT/il/Wide.dll")" WideKt >/dev/null 2>&1 \
	|| die "facadegen failed"
int='{"t":"fqn","name":"Int"}'
ints="$int"; for _ in $(seq 2 17); do ints+=",$int"; done
want="\"name\":\"accept\",\"ret\":$int,\"mods\":{},\"params\":[{\"name\":\"cb\",\"type\":{\"t\":\"fn\",\"suspend\":false,\"ret\":$int,\"params\":[$ints]}}]"
if ! grep -qF "$want" "$OUT/wide.meta"; then
	echo "FAIL  facadegen did not restore KFunc\`18 as a Kotlin function type" >&2
	cat "$OUT/wide.meta" >&2
	exit 1
fi

info "PASS  wide synthetic delegates (kotc -> bir2cir -> ilemit; run + KFunc\`18/KAction\`17 + facadegen restore)"
