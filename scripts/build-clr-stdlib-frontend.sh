#!/usr/bin/env bash
# Build the CLR frontend stdlib jar (kotc's -classpath input, replacing kotlin-stdlib.jar). See memory
# frontend-stdlib-jar-plan. The .kotlin_builtins are now generated FROM OUR sources by -Xoutput-builtins-metadata
# (step 4) -- NOT injected from a JVM kotlin-stdlib jar (the old "jar uf" hack is gone). The kotlin.coroutines
# package-fragment marker (runtime/stdlib/clr/builtins/Coroutines.kt) is what makes that flag not crash.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; cd "$ROOT"
LIBCP="$(echo toolchain/kotc/build/install/kotc/lib/*.jar | tr ' ' ':')"
OUT="$ROOT/build/clr-stdlib-frontend-jvm"; STAGE="$OUT/staged-builtins"; STAGE2="$OUT/staged-arrays"; STAGE3="$OUT/staged-jvm"
JAR="$OUT/kotlin-stdlib-clr-frontend.jar"
rm -rf "$OUT"; mkdir -p "$STAGE" "$STAGE2" "$STAGE3"
# 1. builtins staged with @JvmBuiltin + @SuppressBytecodeGeneration (skip JVM codegen of Array/IntArray)
while IFS= read -r f; do
  rel="${f#$ROOT/runtime/stdlib/clr/builtins/}"; mkdir -p "$STAGE/$(dirname "$rel")"
  { echo "@file:kotlin.internal.JvmBuiltin"; echo "@file:kotlin.internal.SuppressBytecodeGeneration"; cat "$f"; } > "$STAGE/$rel"
done < <(find "$ROOT/runtime/stdlib/clr/builtins" -name '*.kt')
# 2. _ArraysClr.kt contentDeep* get @JvmName (# delimiter -- @ clashes with the @ in @JvmName)
sed -e 's#\(public actual inline infix fun <T> Array<out T>\.contentDeepEquals\)#@kotlin.jvm.JvmName("contentDeepEqualsInline")\n\1#' \
    -e 's#\(public actual inline fun <T> Array<out T>\.contentDeepHashCode\)#@kotlin.jvm.JvmName("contentDeepHashCodeInline")\n\1#' \
    -e 's#\(public actual inline fun <T> Array<out T>\.contentDeepToString\)#@kotlin.jvm.JvmName("contentDeepToStringInline")\n\1#' \
    runtime/stdlib/clr/generated/_ArraysClr.kt > "$STAGE2/_ArraysClr.kt"
# 3. kotlin.jvm.JvmName ACTUAL (our common JvmName is @OptionalExpectation -> platform needs an actual)
cat > "$STAGE3/JvmNameActual.kt" <<'KT'
package kotlin.jvm
@Target(AnnotationTarget.FILE, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY_GETTER, AnnotationTarget.PROPERTY_SETTER)
@MustBeDocumented
public actual annotation class JvmName(actual val name: String)
KT
mapfile -t COMMON   < <(find runtime/stdlib/common/src -name '*.kt')
mapfile -t SRC      < <(find runtime/stdlib/src -name '*.kt')
mapfile -t UNSIGNED < <(find runtime/stdlib/unsigned/src -name '*.kt')
mapfile -t BUILTINS < <(find "$STAGE" -name '*.kt')
mapfile -t CLR_PLAT < <(find runtime/stdlib/clr -name '*.kt' ! -path 'runtime/stdlib/clr/builtins/*' ! -name '_ArraysClr.kt')
CLR_PLAT+=("$STAGE2/_ArraysClr.kt" "$STAGE3/JvmNameActual.kt")
COMMON_SOURCES=("${COMMON[@]}" "${SRC[@]}" "${UNSIGNED[@]}"); COMMON_CSV="$(IFS=,; echo "${COMMON_SOURCES[*]}")"
OPTIN="-opt-in=kotlin.ExperimentalUnsignedTypes,kotlin.experimental.ExperimentalTypeInference,kotlin.contracts.ExperimentalContracts,kotlin.ExperimentalMultiplatform,kotlin.ExperimentalStdlibApi,kotlin.ExperimentalSubclassOptIn,kotlin.io.encoding.ExperimentalEncodingApi,kotlin.time.ExperimentalTime,kotlin.uuid.ExperimentalUuidApi"
# 3b. byref/ClrRef are kotc-INJECTED pure intrinsics (FIR generation extension), absent under stock K2JVMCompiler. The
#     stdlib's atomics use them internally (pass a field by reference to Interlocked). Compile a tiny BINARY stub jar and
#     put it on the main build's -classpath: resolved at compile time, but NOT a source -> neither serialized into the
#     builtins metadata (which requires the package set == the standard builtin packages) nor packed into the frontend
#     jar -> at APP compile the jar has no byref/ClrRef and kotc's own injection supplies them (no duplicate).
KSTDLIB="$(echo toolchain/kotc/build/install/kotc/lib/kotlin-stdlib-*.jar)"
STUBJAR="$OUT/clr-intrinsics-stub.jar"
cat > "$STAGE3/ClrByRefStubSrc.kt" <<'KT'
package kotlin.clr
public class ClrRef<T>
public fun <T> byref(x: T): ClrRef<T> = throw UnsupportedOperationException("frontend-jar byref stub")
KT
java -cp "$LIBCP" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler "$STAGE3/ClrByRefStubSrc.kt" \
  -classpath "$KSTDLIB" -Xallow-kotlin-package -d "$STUBJAR"

# 4. compile -- -Xoutput-builtins-metadata makes K2 WRITE the .kotlin_builtins FROM OUR sources (no JVM injection).
#    It used to crash ("builtins must span ALL builtin pkgs") only because kotlin.coroutines had no builtin package
#    fragment; runtime/stdlib/clr/builtins/Coroutines.kt now provides it (mirrors upstream
#    libraries/stdlib/jvm/builtins/Coroutines.kt). The other builtin pkgs (annotation/internal/ranges/reflect) are
#    spanned by their sources under runtime/stdlib/src via -Xcompile-builtins-as-part-of-stdlib (package-based).
java -cp "$LIBCP" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler \
  "${COMMON[@]}" "${SRC[@]}" "${UNSIGNED[@]}" "${BUILTINS[@]}" "${CLR_PLAT[@]}" \
  -no-stdlib -classpath "$STUBJAR" -Xallow-kotlin-package -Xexpect-actual-classes -Xstdlib-compilation -Xcontext-parameters \
  -Xmulti-platform -Xcommon-sources="$COMMON_CSV" $OPTIN \
  -Xcompile-builtins-as-part-of-stdlib -Xoutput-builtins-metadata -Xuse-14-inline-classes-mangling-scheme -d "$JAR"
# 5. verify -- the compiler itself wrote the 8 .kotlin_builtins; NO JVM kotlin-stdlib injection (the old hack is gone).
B="$(unzip -l "$JAR" 2>/dev/null | grep -c kotlin_builtins)"
echo "frontend jar: $JAR ($(stat -c%s "$JAR") bytes, $B builtins generated from source)"
unzip -l "$JAR" 2>/dev/null | grep -oE 'kotlin/[a-z/]*\.kotlin_builtins' | sort -u
[ "$B" -ge 8 ] || { echo "ERROR: expected >=8 .kotlin_builtins generated, got $B" >&2; exit 1; }
