#!/usr/bin/env bash
# Regression gate for function types wider than System.Func/Action supports: System.Func tops out at 16
# value parameters plus TResult (Func`17); Kotlin function values can be wider, so ilemit synthesizes
# module-local delegate types DotKt.Runtime.CompilerServices.KFunc`N / KAction`N when needed. Drives
# cases/il-widedeleg/wide.kt (17-arg function values) through the REAL pipeline — kotc -> bir2cir ->
# ilemit, the same single path every other gate uses. Runs the app, checks the synthesized delegate types
# exist in the dll, and that
# facadegen restores the wide type as a Kotlin function type. Exits nonzero on any failure.
source "$(dirname "$0")/lib.sh"

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

# Unconditional tool builds: the gate tests the CURRENT sources. Stdlib artifact roles mirror verify-il:
# the frontend KLIB is kotc's -classpath (kotlin.* comes from the klib, never facadegen), the REFERENCE
# dll feeds bir2cir's @Clr labels, the RUNTIME dll backs println at run time.
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1
build_tool ilemit; build_tool bir2cir; build_tool facadegen
need_fe_klib; need_stdlib_ref; need_stdlib_rt

"$KOTC" "$ROOT/cases/il-widedeleg" -no-stdlib -classpath "$FE_KLIB" -d "$OUT/bir" >/dev/null 2>&1 \
	|| die "kotc failed on cases/il-widedeleg"
dotnet "$BIR2CIR_DLL" "$OUT/cir" --ref "$STDLIB_REF_DLL" "$OUT/bir"/*.bir.json >/dev/null 2>&1 \
	|| die "bir2cir failed"
dotnet "$ILEMIT_DLL" "$OUT/il" Wide --ref "$STDLIB_RT_DLL" "$OUT/cir"/*.cir.json >/dev/null 2>&1 \
	|| die "ilemit failed"
cp "$STDLIB_RT_DLL" "$OUT/il/"

expected="$(printf '17\n17\n17')"
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

# Round-trip surface: facadegen must restore the KFunc`18-typed parameter as a Kotlin function type.
# facadegen reads the delegate's Invoke signature (17 params + Int return) directly, regardless of arity
# or the [CompilerGenerated] stamp, and emits it as the JSON function-type node `{"t":"fn",...}` in the
# FIR-injection meta. Assert `accept`'s `cb` param is that fn node with 17 Int params and an Int return.
REFPACK="$(ls -d /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net10.0 2>/dev/null | sort -V | tail -1)"
RUNTIMEPACK="$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/* 2>/dev/null | sort -V | tail -1)"
REFS="$(ls "$REFPACK"/*.dll "$RUNTIMEPACK"/*.dll | tr '\n' ';')$STDLIB_RT_DLL;$OUT/il/Wide.dll"
dotnet "$FACADEGEN_DLL" --meta "$OUT/wide.meta" --refs "$REFS" WideKt >/dev/null 2>&1 \
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
