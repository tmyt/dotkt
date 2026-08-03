#!/usr/bin/env bash
# Regression gate for function types wider than System.Func/Action supports: System.Func tops out at 16
# value parameters plus TResult (Func`17), so Kotlin arities 17..22 use DotKt.Runtime.CompilerServices.KAction`17..22
# / KFunc`18..23 — the CANONICAL family, defined ONCE in the stdlib (#220) and referenced by everything else.
# Drives tests/special/wide-delegates/wide.kt (17-arg function values) through the REAL pipeline — kotc -> bir2cir ->
# ilemit, the same single path every other gate uses. Runs the app, checks the app DEFINES no delegate of its own
# (delegate-typedefs.cs reads TypeDef rows; `strings` cannot tell a definition from a reference) while the stdlib
# defines the whole family, and that dll2klib restores the wide type as a Kotlin function type consumable from a
# second module. Exits nonzero on any failure.
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

# The wide shapes must bind the STDLIB's canonical delegate types, so the app defines none of its own — an
# assembly-local definition would be a distinct nominal type and therefore an invalid public ABI. The whole
# canonical family is present in the stdlib whether or not this compilation used it.
typedefs() { # <assembly> -> one SHAPE line per DEFINED KFunc`N/KAction`N (see delegate-typedefs.cs)
	local out
	out="$(dotnet run "$ROOT/tests/special/wide-delegates/delegate-typedefs.cs" -- "$1")" \
		|| die "delegate-typedefs.cs failed on $1"
	printf '%s\n' "$out"
}
app_defs="$(typedefs "$OUT/il/Wide.dll")"
if [[ -n "${app_defs//[[:space:]]/}" ]]; then
	echo "FAIL  emitted assembly DEFINES a wide delegate instead of referencing the stdlib's:" >&2
	printf '%s\n' "$app_defs" >&2
	exit 1
fi
# The WHOLE canonical family is in the stdlib whether or not this compilation used it — all six pairs, not just the
# edges, since a gap in the middle would only surface as an unbuildable program at that one arity.
stdlib_defs="$(typedefs "$STDLIB_RT_DLL")"
for arity in $(seq 17 22); do
	for expected in "KAction\`$arity" "KFunc\`$((arity + 1))"; do
		grep -q "^DotKt\.Runtime\.CompilerServices\.$expected<" <<<"$stdlib_defs" \
			|| die "the runtime stdlib does not define the canonical DotKt.Runtime.CompilerServices.$expected"
	done
done
(( $(grep -c . <<<"$stdlib_defs") == 12 )) \
	|| die "the runtime stdlib defines $(grep -c . <<<"$stdlib_defs") wide delegates, expected exactly the 12 canonical ones"
# Each is a real variant delegate: contravariant parameters and (for KFunc) a covariant result, exactly as
# System.Func/Action declare them. Without that a Kotlin `(Any,…)->String` cannot be stored in a `(String,…)->Any`
# slot, which the frontend accepts and the narrow arities already support.
grep -q '^DotKt\.Runtime\.CompilerServices\.KFunc`18<in T1,in T2,in T3,in T4,in T5,in T6,in T7,in T8,in T9,in T10,in T11,in T12,in T13,in T14,in T15,in T16,in T17,out TResult>' <<<"$stdlib_defs" \
	|| die "the canonical KFunc\`18 is not declared variant (in T…, out TResult)"
grep -q '^DotKt\.Runtime\.CompilerServices\.KAction`17<in T1,in T2,in T3,in T4,in T5,in T6,in T7,in T8,in T9,in T10,in T11,in T12,in T13,in T14,in T15,in T16,in T17>' <<<"$stdlib_defs" \
	|| die "the canonical KAction\`17 is not declared variant (in T…)"
# The reference twin must expose the identical DECLARATIONS — generic arity and variance, base, type attributes and
# the full Invoke/.ctor signatures, not merely the same type names. The Kotlin round-trip attribute is compared
# separately: the runtime build emits no DotKt attribute CLASSES at all, so only the reference twin carries it.
diff <(typedefs "$STDLIB_REF_DLL" | sed 's/ cattrs=[^ ]*//') <(sed 's/ cattrs=[^ ]*//' <<<"$stdlib_defs") >/dev/null \
	|| die "the stdlib reference and runtime twins expose different canonical delegate declarations"
grep -q 'cattrs=[^ ]*DotKt.Runtime.CompilerServices.KotlinFunctionAttribute' <<<"$(typedefs "$STDLIB_REF_DLL")" \
	|| die "the stdlib REFERENCE twin does not stamp [KotlinFunction] on the canonical family"

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

consumer_defs="$(typedefs "$OUT/consumer-il/WideConsumer.dll")"
[[ -z "$consumer_defs" ]] || die "the consuming module defined its own wide delegate: $consumer_defs"

info "PASS  canonical wide delegates (kotc -> bir2cir -> ilemit; run + stdlib-defined KFunc\`18/KAction\`17 with no module-local definition + dll2klib/KLIB re-consumption)"
