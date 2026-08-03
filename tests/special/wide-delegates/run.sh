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
app_defs="$(typedefs "$OUT/il/Wide.dll" | grep . || true)"
if [[ -n "$app_defs" ]]; then
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

# --- the DEFERRED range: Kotlin arity >= BuiltInFunctionArity.BIG_ARITY (23) ------------------------------------
# #220 stops the canonical family at 22 and leaves 23+ to a variadic big-arity ABI, so every assembly still mints
# its own KFunc`N/KAction`N there and the two sides of a module boundary hold different nominal types for one
# declared shape. That is a real, currently-shipping limitation; it is recorded HERE rather than left silent, with
# one entry per cell it breaks, each pinned to the diagnostic its reason names so an unrelated failure cannot hide
# behind it. They all close together, when the variadic ABI gives arity >= 23 a single definition — at which point
# these entries go stale and the shared baseline verdict reddens until they are pruned.
declare -A WIDE_XFAIL=(
	[arity23-return-position]='#220 defers arity >= 23: with no canonical definition the consumer mints its OWN KFunc`24 and invokes the producer'"'"'s value through it — ILVerify StackUnexpected (runtime-safe: identical layout; the JIT does not verify). Closes when the variadic big-arity ABI gives arity >= 23 one definition.'
	[arity23-nested-in-a-generic]='#220 defers arity >= 23: a wide function type NESTED in a generic (List<(...)->R>) is a fully concrete signature node, so ilemit compares it by exact Reflection identity against the producer'"'"'s nominally different local delegate and aborts the call with "no referenced method matches the resolved descriptor". Closes with the same variadic big-arity ABI.'
	[arity23-two-arities-in-one-producer]='#220 defers arity >= 23: a producer declaring TWO deferred arities defines two same-named KFunc`N, and dll2klib keys _delegateDefinitions by the arity-STRIPPED name while ArityNames renames the clashing family — so the KLIB restores KFunc24/KFunc25 as ordinary classes and the consumer fails in the FRONTEND. Canonical 17..22 no longer clashes because producers define nothing there. Closes with the same variadic big-arity ABI (or by keying the delegate map on the full metadata name).'
)

deferred_out="$OUT/deferred23"
mkdir -p "$deferred_out/src"
# Two producers, because the cells would otherwise mask each other: `deferred23` carries ONE deferred arity, so the
# return and nested cells fail for their own reasons; `deferred24` carries TWO, which is the arity-clash shape (and
# also the shape that must not reach back into the canonical range — dll2klib's clash set is a union over the WHOLE
# reference universe, so a bare-name rename here would otherwise rename the stdlib's KFunc too).
mkdir -p "$deferred_out/clash-src"
wide_params="$(printf 'Int, %.0s' $(seq 1 22))Int"
wide_args="$(seq -s ', ' 1 23)"
{
	printf 'package deferred23\n\n'
	printf 'fun ret23(): (%s) -> Int = { %s -> p1 + p23 }\n' "$wide_params" "$(printf 'p%d, ' $(seq 1 22))p23"
	printf 'fun applyNested23(fs: List<(%s) -> Int>): Int = fs[0](%s)\n' "$wide_params" "$wide_args"
	printf 'fun nested23(): List<(%s) -> Int> = listOf({ %s -> p1 * p23 })\n' "$wide_params" "$(printf 'p%d, ' $(seq 1 22))p23"
} > "$deferred_out/src/Producer.kt"
{
	printf 'package deferred24\n\n'
	printf 'fun param23(f: (%s) -> Int): Int = f(%s)\n' "$wide_params" "$wide_args"
	printf 'fun param24(f: (%s, Int) -> Int): Int = f(%s, 24)\n' "$wide_params" "$wide_args"
} > "$deferred_out/clash-src/Producer.kt"
mkdir -p "$deferred_out/ret" "$deferred_out/nested" "$deferred_out/clash"
printf 'fun main() { println(deferred23.ret23()(%s)) }\n' "$wide_args" > "$deferred_out/ret/Main.kt"
printf 'fun main() { println(deferred23.applyNested23(deferred23.nested23())) }\n' > "$deferred_out/nested/Main.kt"
printf 'fun main() { println(deferred24.param24({ %s -> p1 + p24 })) }\n' \
	"$(printf 'p%d, ' $(seq 1 23))p24" > "$deferred_out/clash/Main.kt"

# <dir> <asm> <classpath-extra-klib> <extra-ref-dll...> -> 0 iff the module compiled and emitted; the combined
# tool output of the FAILING stage is left in $deferred_log so a cell can pin the diagnostic it claims.
deferred_log=""
deferred_build() {
	local src="$1" asm="$2" klib="$3"; shift 3
	local cp="$FE_KLIB"; [[ -n "$klib" ]] && cp+="$cp_sep$klib"
	mkdir -p "$deferred_out/$asm"
	deferred_log="$("$KOTC" "$src" -no-stdlib -classpath "$cp" -d "$deferred_out/$asm/bir" 2>&1)" || return 1
	deferred_log="$(dotnet "$BIR2CIR_DLL" "$deferred_out/$asm/cir" \
		--compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_REF_DLL" "$@")" \
		"$deferred_out/$asm/bir"/*.bir.json 2>&1)" || return 1
	deferred_log="$(dotnet "$ILEMIT_DLL" "$deferred_out/$asm/il" "$asm" \
		--compile-refs "$(refset_join "$FRAMEWORK_COMPILE_REFS" "$STDLIB_RT_DLL" "$@")" \
		--runtime-refs "$(refset_join "$STDLIB_RT_DLL" "$@")" \
		--target-framework-moniker "$DOTKT_TARGET_FRAMEWORK_MONIKER" \
		"$deferred_out/$asm/cir"/*.cir.json 2>&1)" || return 1
	deferred_log=""
}

# Record <cell> only when the build failed WITH the diagnostic the baseline reason names; any other failure is a
# different defect and must reach xfail_diff under its own name rather than be absorbed here.
deferred_expect_failure() { # <cell> <expected diagnostic substring>
	if deferred_build "${@:3}"; then return; fi
	if [[ "$deferred_log" == *"$2"* ]]; then deferred_fails+=("$1"); else deferred_fails+=("$1-unexpected-diagnostic"); fi
}

deferred_build "$deferred_out/src" Deferred23 "" || die "the arity-23 producer itself must still compile: $deferred_log"
deferred_producer="$deferred_out/Deferred23/il/Deferred23.dll"
dotnet "$DLL2KLIB_DLL" "$deferred_producer" "$deferred_out/Deferred23.klib" >/dev/null
deferred_build "$deferred_out/clash-src" Deferred24 "" || die "the two-arity deferred producer must still compile: $deferred_log"
clash_producer="$deferred_out/Deferred24/il/Deferred24.dll"
dotnet "$DLL2KLIB_DLL" "$clash_producer" "$deferred_out/Deferred24.klib" >/dev/null
declare -a deferred_fails=()
deferred_expect_failure arity23-nested-in-a-generic 'no referenced method matches the resolved descriptor' \
	"$deferred_out/nested" Deferred23Nested "$deferred_out/Deferred23.klib" "$deferred_producer"
deferred_expect_failure arity23-two-arities-in-one-producer 'argument type mismatch' \
	"$deferred_out/clash" Deferred24Consumer "$deferred_out/Deferred24.klib" "$clash_producer"
if deferred_build "$deferred_out/ret" Deferred23Ret "$deferred_out/Deferred23.klib" "$deferred_producer"; then
	cp "$STDLIB_RT_DLL" "$deferred_producer" "$deferred_out/Deferred23Ret/il/"
	ilv="$(find "$HOME/.dotnet" -name 'ILVerify.dll' 2>/dev/null | head -1)"
	rtdir="$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/* 2>/dev/null | sort -V | tail -1)"
	ilv_out="$(dotnet "$ilv" "$deferred_out/Deferred23Ret/il/Deferred23Ret.dll" \
		-r "$rtdir/*.dll" -r "$deferred_out/Deferred23Ret/il/*.dll" 2>&1 || true)"
	# The cell claims one specific finding — a StackUnexpected in the consumer's own `main`. Any OTHER ILVerify
	# error in this assembly is a different defect and must not be absorbed by this baseline entry.
	if grep -q 'Error \[StackUnexpected\].*MainKt::main' <<<"$ilv_out"; then
		deferred_fails+=(arity23-return-position)
	elif grep -qE '\[IL\]: Error' <<<"$ilv_out"; then
		deferred_fails+=(arity23-return-position-unexpected-finding)
	fi
else
	deferred_fails+=(arity23-return-position-did-not-emit)
fi

# The CANONICAL range must survive a reference universe that also contains a module with two DEFERRED arities.
# dll2klib computes ONE arity-clash set for the whole batch and pushes it into every worker, so this has to be a
# BATCH projection (`--out <dir> @rsp`, the shape MSBuild drives) — projecting one dll at a time cannot see the
# other module's clash and would pass either way. Hard assertion, not an XFAIL: 17..22 is the shipped ABI.
printf '%s\n%s\n' "$OUT/il/Wide.dll" "$clash_producer" > "$deferred_out/clash-batch.rsp"
dotnet "$DLL2KLIB_DLL" --out "$deferred_out/clash-klibs" --jobs 2 @"$deferred_out/clash-batch.rsp" >/dev/null
clash_cp="$FE_KLIB"
while IFS= read -r klib; do clash_cp+="$cp_sep$klib"; done \
	< <(find "$OUT/reference-klibs" -maxdepth 1 -type f -name '*.klib' | LC_ALL=C sort)
clash_cp+="$cp_sep$deferred_out/clash-klibs/Wide.klib$cp_sep$deferred_out/clash-klibs/Deferred24.klib"
"$KOTC" "$OUT/consumer.kt" -no-stdlib -classpath "$clash_cp" -d "$deferred_out/canonical-bir" >/dev/null 2>&1 \
	|| die "a referenced module with two DEFERRED arities broke the canonical 17..22 re-import (arity-clash rename)"

xfail_diff wide-delegates WIDE_XFAIL "${deferred_fails[@]}"
xfail_gate_is_clean || die "the deferred arity>=23 baseline no longer describes reality (see NEW-FAIL/FIXED above)"

info "PASS  canonical wide delegates (kotc -> bir2cir -> ilemit; run + stdlib-defined KFunc\`18/KAction\`17 with no module-local definition + dll2klib/KLIB re-consumption)"
