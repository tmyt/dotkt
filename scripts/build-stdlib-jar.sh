#!/usr/bin/env bash
# Build the CLR FRONTEND stdlib jar (kotlin-stdlib-clr-frontend.jar — kotc's -classpath input, replacing
# the JVM kotlin-stdlib.jar whose java.util.* typealiases leaked into the frontend). Compiles OUR stdlib
# sources with the stock K2JVMCompiler (from the kotc install's lib jars); the 8 .kotlin_builtins are
# generated FROM OUR sources by -Xoutput-builtins-metadata — NOT injected from a JVM kotlin-stdlib jar
# (the old "jar uf" hack is gone). The kotlin.coroutines package-fragment marker
# (libraries/stdlib/clr/builtins/Coroutines.kt) is what keeps that flag from crashing. Inputs:
# libraries/stdlib sources + the kotc install. Output: build/clr-stdlib-frontend-jvm/ (wiped first!) with
# the jar + staging dirs. See MEMORY frontend-stdlib-jar-plan.
source "$(dirname "$0")/lib.sh"

usage() {
	cat <<EOF
usage: $SCRIPT_NAME
Builds $FE_JAR from the libraries/stdlib sources (no flags). -h for this help.
Exits nonzero if the jar was not produced or has fewer than 8 generated .kotlin_builtins.
EOF
}
while (( $# )); do
	case "$1" in
		-h|--help) usage; exit 0 ;;
		*) usage_error "unknown argument '$1'" ;;
	esac
done

need_kotc
cd "$ROOT"
LIBCP="$(echo toolchain/kotc/build/install/kotc/lib/*.jar | tr ' ' ':')"
OUT="$ROOT/build/clr-stdlib-frontend-jvm"; STAGE="$OUT/staged-builtins"; STAGE2="$OUT/staged-arrays"; STAGE3="$OUT/staged-jvm"
JAR="$FE_JAR"
rm -rf "$OUT"; mkdir -p "$STAGE" "$STAGE2" "$STAGE3"

# 1. builtins staged with @JvmBuiltin + @SuppressBytecodeGeneration (skip JVM codegen of Array/IntArray)
while IFS= read -r f; do
	rel="${f#$ROOT/libraries/stdlib/clr/builtins/}"; mkdir -p "$STAGE/$(dirname "$rel")"
	{ echo "@file:kotlin.internal.JvmBuiltin"; echo "@file:kotlin.internal.SuppressBytecodeGeneration"; cat "$f"; } > "$STAGE/$rel"
done < <(find "$ROOT/libraries/stdlib/clr/builtins" -name '*.kt')

# 2. _ArraysClr.kt contentDeep* get @JvmName (# delimiter -- @ clashes with the @ in @JvmName)
sed -e 's#\(public actual inline infix fun <T> Array<out T>\.contentDeepEquals\)#@kotlin.jvm.JvmName("contentDeepEqualsInline")\n\1#' \
    -e 's#\(public actual inline fun <T> Array<out T>\.contentDeepHashCode\)#@kotlin.jvm.JvmName("contentDeepHashCodeInline")\n\1#' \
    -e 's#\(public actual inline fun <T> Array<out T>\.contentDeepToString\)#@kotlin.jvm.JvmName("contentDeepToStringInline")\n\1#' \
    libraries/stdlib/clr/generated/_ArraysClr.kt > "$STAGE2/_ArraysClr.kt"

# 3. kotlin.jvm.JvmName ACTUAL (our common JvmName is @OptionalExpectation -> platform needs an actual)
cat > "$STAGE3/JvmNameActual.kt" <<'KT'
package kotlin.jvm
@Target(AnnotationTarget.FILE, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY_GETTER, AnnotationTarget.PROPERTY_SETTER)
@MustBeDocumented
public actual annotation class JvmName(actual val name: String)
KT
# 3b. kotlin.jvm.JvmInline ACTUAL — same @OptionalExpectation situation as JvmName: without a platform actual in the
#     jar, an APP-side `@JvmInline value class` dies with "declaration annotated with '@OptionalExpectation' can only
#     be used in common module sources" (the JVM-frontend value-class checker REQUIRES @JvmInline, so apps must write it).
cat > "$STAGE3/JvmInlineActual.kt" <<'KT'
package kotlin.jvm
@Target(AnnotationTarget.CLASS)
@MustBeDocumented
public actual annotation class JvmInline
KT
# 3c. (removed) kotlin.clr.blockOn / delay were DROPPED from the stdlib — they are kotlinx/Track-2 primitives
#     re-implemented in the TEST HARNESS over public primitives (docs/design-coroutine-cold-core-task-bridge.md
#     §13). No common `expect` / jar stub actual remains; the core kotlin.clr coroutine surface is `await` ONLY.

mapfile -t COMMON   < <(find libraries/stdlib/common/src -name '*.kt')
mapfile -t SRC      < <(find libraries/stdlib/src -name '*.kt')
mapfile -t UNSIGNED < <(find libraries/stdlib/unsigned/src -name '*.kt')
mapfile -t BUILTINS < <(find "$STAGE" -name '*.kt')
# CONVENTION: libraries/stdlib/clr/taskinterop/ is the jar-EXCLUDED, CLR-build-ONLY source set — the
# Task-facing kotlin.clr surface (Task/Task0/TaskCompletionSource aliases, await, RootContinuation). The
# frontend jar must stay the PURE Kotlin stdlib surface and not carry System.Threading.Tasks-bound symbols
# (docs/design-coroutine-cold-core-task-bridge.md §5); frontend resolution for await consumers rides kotc's
# kotlin.clr injection seam (ClrTypeInjection.kt — bundle-6 P2), NOT this jar. build-stdlib-{ref,rt}.sh DO
# compile taskinterop/ (lib.sh collect_stdlib_sources finds all of clr/), so the declarations + bodies live
# in ref.dll/rt.dll.
mapfile -t CLR_PLAT < <(find libraries/stdlib/clr -name '*.kt' ! -path 'libraries/stdlib/clr/builtins/*' ! -path 'libraries/stdlib/clr/taskinterop/*' ! -name '_ArraysClr.kt')
CLR_PLAT+=("$STAGE2/_ArraysClr.kt" "$STAGE3/JvmNameActual.kt" "$STAGE3/JvmInlineActual.kt")
COMMON_SOURCES=("${COMMON[@]}" "${SRC[@]}" "${UNSIGNED[@]}"); COMMON_CSV="$(IFS=,; echo "${COMMON_SOURCES[*]}")"
# NOTE: the CLR stdlib does not reference the kotc-injected `ClrRef<T>`/`byref` intrinsics — its implicit-byref
# bindings (atomics Interlocked, tryParseInt32, mathDivRemInt) use plain-typed params marked @kotlin.clr.ClrRefArgument
# (a normal stdlib annotation, resolvable under stock K2JVMCompiler). So no `clr-intrinsics-stub.jar` is needed here —
# the stdlib ABI matches the jar's.

# 4. compile -- -Xoutput-builtins-metadata makes K2 WRITE the .kotlin_builtins FROM OUR sources (no JVM injection).
#    It used to crash ("builtins must span ALL builtin pkgs") only because kotlin.coroutines had no builtin package
#    fragment; libraries/stdlib/clr/builtins/Coroutines.kt now provides it (mirrors upstream
#    libraries/stdlib/jvm/builtins/Coroutines.kt). The other builtin pkgs (annotation/internal/ranges/reflect) are
#    spanned by their sources under libraries/stdlib/src via -Xcompile-builtins-as-part-of-stdlib (package-based).
java -cp "$LIBCP" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler \
	"${COMMON[@]}" "${SRC[@]}" "${UNSIGNED[@]}" "${BUILTINS[@]}" "${CLR_PLAT[@]}" \
	-no-stdlib -Xallow-kotlin-package -Xexpect-actual-classes -Xstdlib-compilation -Xcontext-parameters \
	-Xmulti-platform -Xcommon-sources="$COMMON_CSV" $STDLIB_OPTIN \
	-Xcompile-builtins-as-part-of-stdlib -Xoutput-builtins-metadata -Xuse-14-inline-classes-mangling-scheme -d "$JAR"

# 5. verify -- the compiler itself wrote the 8 .kotlin_builtins; NO JVM kotlin-stdlib injection (the old hack is gone).
B="$(unzip -l "$JAR" 2>/dev/null | grep -c kotlin_builtins)"
info "frontend jar: $JAR ($(stat -c%s "$JAR") bytes, $B builtins generated from source)"
unzip -l "$JAR" 2>/dev/null | grep -oE 'kotlin/[a-z/]*\.kotlin_builtins' | sort -u
[[ "$B" -ge 8 ]] || die "expected >=8 .kotlin_builtins generated, got $B"
