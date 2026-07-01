#!/usr/bin/env bash
# DotKt round-trip: a Kotlin assembly compiled by DotKt, consumed AS KOTLIN by another module — the Kotlin
# modifiers with no .NET analog (infix / operator / suspend / top-level) survive the trip. They're stamped onto the
# emitted IL as DotKt.Metadata attributes ([KotlinFunction]/[KotlinFileClass]) by ilemit, then read back by facadegen
# (--meta) and restored on the synthesized FIR by ClrTypeInjection. This is the basis of consuming compiled Kotlin
# libraries (kotlinx-*) as Kotlin. See docs/design-kotlin-metadata-attributes.md.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# kotc resolves the stdlib (kotlin.*) from the CLR FRONTEND JAR (scripts/build-clr-stdlib-frontend.sh), REPLACING the
# JVM kotlin-stdlib.jar (which leaked java.util.* typealiases). kotlinx.coroutines stays a separate jar (the consumer
# awaits suspend funs via runBlocking). The frontend jar is built below once the launcher (its lib jars) exists.
FE_JAR="$ROOT/build/clr-stdlib-frontend-jvm/kotlin-stdlib-clr-frontend.jar"
CORO="$(find "$HOME/.gradle/caches" -name 'kotlinx-coroutines-core-jvm-1.8.0.jar' | head -1)"
CP="$FE_JAR:$CORO"

# Build the toolchain (compiler launcher + ilemit + facadegen + retarget + runtime) once.
"$ROOT/gradlew" -q :kotc:installDist >/dev/null 2>&1
LAUNCHER="$ROOT/toolchain/kotc/build/install/kotc/bin/kotc"
# Frontend stdlib jar (kotc's -classpath input): build once if missing (consumes the kotc lib jars from installDist).
[[ -f "$FE_JAR" ]] || bash "$ROOT/scripts/build-clr-stdlib-frontend.sh" >/dev/null 2>&1
dotnet build "$ROOT/toolchain/ilemit"        -c Release -o "$ROOT/build/ilemit-bin"     -v q --nologo >/dev/null
dotnet build "$ROOT/toolchain/facadegen"     -c Release -o "$ROOT/build/facadegen-bin"  -v q --nologo >/dev/null
dotnet build "$ROOT/toolchain/retarget"      -c Release -o "$ROOT/build/retarget-bin"   -v q --nologo >/dev/null
REFPACK="$(ls -d /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net10.0 2>/dev/null | sort -V | tail -1)"
REFS="$(ls "$REFPACK"/*.dll | tr '\n' ';')"

# kotc emits bare kotlin.* type tokens (the frontend jar resolves the stdlib to our real kotlin.* declarations); bir2cir
# lowers them to the CLR-codegen vocabulary ilemit consumes. So route every emit through bir2cir (mirrors verify-il) —
# feeding BIR straight to ilemit would leave kotlin.* tokens un-lowered ("cannot resolve .NET type kotlin.String"). The
# REFERENCE stdlib supplies bir2cir's @ClrTypeAlias labels (built once if missing; the roundtrip types are pure-Kotlin).
dotnet build "$ROOT/toolchain/bir2cir" -c Release -o "$ROOT/build/bir2cir-bin" -v q --nologo >/dev/null
STDLIB_REF_DLL="$ROOT/build/clr-stdlib/dll/DotKt.Private.Stdlib.dll"
[[ -f "$STDLIB_REF_DLL" ]] || bash "$ROOT/scripts/build-clr-stdlib.sh" --emit >/dev/null 2>&1
# emit_il: drop-in for `ilemit <outdir> <asm> [--ref X]... <bir files...>`, inserting the BIR->CIR (bir2cir) lowering.
emit_il() {
	local out="$1" asm="$2"; shift 2
	local refs=() birs=()
	while (( $# )); do
		if [[ "$1" == --ref ]]; then refs+=(--ref "$2"); shift 2; else birs+=("$1"); shift; fi
	done
	local cir="$out.cir"; rm -rf "$cir"; mkdir -p "$cir"
	local refarg=(); [[ -f "$STDLIB_REF_DLL" ]] && refarg=(--ref "$STDLIB_REF_DLL")
	dotnet "$ROOT/build/bir2cir-bin/bir2cir.dll" "$cir" "${refarg[@]}" "${birs[@]}" >/dev/null 2>&1
	dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$out" "$asm" "${refs[@]}" "$cir"/*.cir.json >/dev/null 2>&1
}

# ----- MARKER round-trip: Kotlin class-nature facts with no faithful .NET analog survive re-consumption -----
# A `fun interface` (SAM), a `sealed` class/interface, and an `enum class` lower to a plain interface / abstract-class /
# CLR-enum, LOSING the Kotlin nature. ilemit stamps [KotlinFunInterface]/[KotlinSealed]; facadegen reads them back
# (`funinterface`/`sealed` meta lines); ClrTypeInjection restores `status.isFun` / `Modality.SEALED`. (Runs BEFORE the
# suspend section below, which is blocked on the deferred coroutine lowering.) See docs/dotkt-semantics.md §10.
M="$ROOT/build/roundtrip-markers"; rm -rf "$M"; mkdir -p "$M/lib" "$M/app" "$M/rogue" "$M/libbir" "$M/libil" "$M/appbir" "$M/appil"
cat > "$M/lib/lib.kt" <<'EOF'
package shapes
fun interface Handler { fun on(x: Int): Int }
sealed interface Shape { fun area(): Int }
class Circle(val r: Int) : Shape { override fun area(): Int = r * r * 3 }
class Square(val s: Int) : Shape { override fun area(): Int = s * s }
enum class Color { RED, GREEN, BLUE }
fun runHandler(h: Handler, v: Int): Int = h.on(v)
fun describe(s: Shape): String = "area=" + s.area()
EOF
cat > "$M/app/app.kt" <<'EOF'
import shapes.Handler
import shapes.Shape
import shapes.Circle
import shapes.Square
import shapes.Color
import shapes.runHandler
import shapes.describe
fun classify(s: Shape): String = when (s) {   // exhaustive over the restored sealed hierarchy — no `else` needed
    is Circle -> "circle"
    is Square -> "square"
}
fun main() {
    val h = object : Handler { override fun on(x: Int): Int = x * 10 }
    println(runHandler(h, 5))       // fun interface (nature restored) usable across module
    println(describe(Circle(2)))    // sealed supertype usable across module
    println(classify(Square(3)))    // exhaustive `when` over the restored sealed type
    println(Color.GREEN)            // enum value access (non-regression)
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$M/lib" -no-stdlib -classpath "$CP" -d "$M/libbir" >/dev/null 2>&1
emit_il "$M/libil" MarkLib "$M/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$M/libil/MarkLib.dll" --refs "$REFS" >/dev/null 2>&1
"$LAUNCHER" --scan-imports --output "$M/imports.txt" "$M/app"/*.kt >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$M/meta" --refs "$REFS$M/libil/MarkLib.dll" --import-list "$M/imports.txt" >/dev/null 2>&1
CLR_TYPES_METADATA="$M/meta" "$LAUNCHER" "$M/app" -no-stdlib -classpath "$CP" -d "$M/appbir" >/dev/null 2>&1
emit_il "$M/appil" MarkApp --ref "$M/libil/MarkLib.dll" "$M/appbir"/*.bir.json >/dev/null 2>&1
cp "$M/libil/MarkLib.dll" "$M/appil/"
mkexpected="$(printf '50\narea=12\nsquare\nGREEN')"
mkactual="$(dotnet "$M/appil/MarkApp.dll" 2>/dev/null)"
# NEGATIVE: `sealed` is cross-module-enforced — a rogue subclass in another module MUST be rejected (proves Modality.SEALED restored).
cat > "$M/rogue/rogue.kt" <<'EOF'
import shapes.Shape
class Rogue : Shape { override fun area(): Int = 0 }
EOF
if CLR_TYPES_METADATA="$M/meta" "$LAUNCHER" "$M/rogue" -no-stdlib -classpath "$CP" -d "$M/roguebir" >/dev/null 2>&1; then rogue_ok=1; else rogue_ok=0; fi
if [[ "$mkactual" == "$mkexpected" && "$rogue_ok" == 0 ]]; then
    echo "PASS  roundtrip-markers (fun interface nature; sealed modality+exhaustive-when+cross-module enforcement; enum)"
else
    echo "FAIL  roundtrip-markers"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n--- rogue accepted (want reject): %s ---\n' "$mkexpected" "$mkactual" "$rogue_ok"; exit 1
fi

R="$ROOT/build/roundtrip"; rm -rf "$R"; mkdir -p "$R/lib" "$R/app" "$R/libbir" "$R/libil" "$R/appbir" "$R/appil"

# The Kotlin LIBRARY: a class with infix/operator/(member)suspend members + top-level (plain + suspend) functions.
cat > "$R/lib/lib.kt" <<'EOF'
class Vec(val x: Int, val y: Int) {
    infix fun dot(o: Vec): Int = x * o.x + y * o.y
    operator fun plus(o: Vec): Vec = Vec(x + o.x, y + o.y)
    fun show(): String = "(" + x + ", " + y + ")"
    suspend fun scaleAsync(k: Int): Vec = Vec(x * k, y * k)   // member suspend returning a USER type
}
fun greet(name: String): String = "Hi, " + name
suspend fun addAsync(a: Int, b: Int): Int = a + b
EOF

# The Kotlin CONSUMER: uses every restored modifier with idiomatic Kotlin syntax.
cat > "$R/app/app.kt" <<'EOF'
import kotlinx.coroutines.runBlocking
fun main() {
    val a = Vec(1, 2)
    val b = Vec(3, 4)
    println(a dot b)                          // infix notation
    println((a + b).show())                   // operator +
    println(greet("Vec"))                     // top-level function (no qualifier)
    println(runBlocking { addAsync(20, 22) })       // top-level suspend fun, awaited
    println(runBlocking { a.scaleAsync(3) }.show())  // member suspend fun returning a user type, awaited
}
EOF

# 1. compile + emit + retarget the library (the emit stamps [KotlinFunction]/[KotlinFileClass]).
CLR_TYPES_METADATA="" "$LAUNCHER" "$R/lib" -no-stdlib -classpath "$CP" -d "$R/libbir" >/dev/null 2>&1
emit_il "$R/libil" KLib "$R/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$R/libil/KLib.dll" --refs "$REFS" >/dev/null 2>&1
# 2. facadegen --meta reads the attributes back into the injection metadata.
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$R/k.meta" --refs "$REFS$R/libil/KLib.dll" Vec LibKt >/dev/null 2>&1
# 3. compile the consumer WITH the metadata (the injector restores infix/operator/suspend/top-level on FIR).
CLR_TYPES_METADATA="$R/k.meta" "$LAUNCHER" "$R/app" -no-stdlib -classpath "$CP" -d "$R/appbir" >/dev/null 2>&1
emit_il "$R/appil" KApp --ref "$R/libil/KLib.dll" "$R/appbir"/*.bir.json >/dev/null 2>&1
cp "$R/libil/KLib.dll" "$R/appil/"

expected="$(printf '11\n(4, 6)\nHi, Vec\n42\n(3, 6)')"
actual="$(dotnet "$R/appil/KApp.dll" 2>/dev/null)"
if [[ "$actual" == "$expected" ]]; then
    echo "PASS  roundtrip (infix / operator / suspend / top-level restored from a DotKt assembly)"
else
    echo "FAIL  roundtrip"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$expected" "$actual"; exit 1
fi

# ----- PACKAGED round-trip: Kotlin packages project to .NET namespaces, consumed via package-qualified imports -----
# Also guards the correctness bug where same-named classes in different packages collided at the root namespace.
G="$ROOT/build/roundtrip-pkg"; rm -rf "$G"; mkdir -p "$G/lib" "$G/app" "$G/libbir" "$G/libil" "$G/appbir" "$G/appil"
cat > "$G/lib/geom.kt" <<'EOF'
package geom
enum class Dir { NORTH, EAST }
class Vec(var x: Int, var y: Int) {
    infix fun dot(o: Vec): Int = x * o.x + y * o.y
    val mag2: Int get() = x * x + y * y          // property with a custom getter
}
operator fun Vec.plus(o: Vec): Vec = Vec(x + o.x, y + o.y)   // top-level extension operator
val Vec.manhattan: Int get() = x + y                          // extension property
fun sumAll(vararg xs: Int): Int { var s = 0; for (v in xs) s += v; return s }   // vararg
fun tagged(s: String = "def"): String = s                    // default argument
fun orNone(s: String?): String = s ?: "none"                 // nullable parameter
fun greet(name: String): String = "Hi, " + name
inline fun <reified T> typeName(): String = T::class.simpleName ?: "?"   // reified inline -> generic method
inline fun forEach3(a: Int, b: Int, c: Int, action: (Int) -> Unit) { action(a); action(b); action(c) }
EOF
# A class with the SAME simple name in a DIFFERENT package — must not collide (they used to, at the root namespace).
cat > "$G/lib/other.kt" <<'EOF'
package other
class Vec(val tag: String)
EOF
cat > "$G/app/app.kt" <<'EOF'
import geom.Vec
import geom.Dir
import geom.greet
import geom.typeName
import geom.forEach3
import geom.plus
import geom.manhattan
import geom.sumAll
import geom.tagged
import geom.orNone
fun firstEven(): Int {
    forEach3(1, 3, 4) { if (it % 2 == 0) return it }   // NON-LOCAL return through a CROSS-MODULE inline lambda
    return -1
}
fun main() {
    println(Vec(1, 2) dot Vec(3, 4))   // geom.Vec, infix
    println(greet("pkg"))              // top-level via `import geom.greet`
    println(Dir.EAST)                  // enum in a package
    println(typeName<String>())        // cross-module reified inline -> generic method call
    println(firstEven())               // cross-module inline + lambda + non-local return -> spliced body
    val v = Vec(3, 4); println(v.mag2) // property (custom getter)
    v.x = 6; println(v.mag2)           // mutable property write
    println((Vec(1, 2) + Vec(3, 4)).mag2)  // top-level extension operator + property
    println(sumAll(1, 2, 3, 4))        // vararg
    println(Vec(3, 4).manhattan)       // extension property
    println(tagged())                  // default argument omitted
    println(orNone(null))              // nullable param (null passable)
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$G/lib" -no-stdlib -classpath "$CP" -d "$G/libbir" >/dev/null 2>&1
emit_il "$G/libil" GeomLib "$G/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$G/libil/GeomLib.dll" --refs "$REFS" >/dev/null 2>&1
"$LAUNCHER" --scan-imports --output "$G/imports.txt" "$G/app"/*.kt >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$G/meta" --refs "$REFS$G/libil/GeomLib.dll" --import-list "$G/imports.txt" >/dev/null 2>&1
CLR_TYPES_METADATA="$G/meta" "$LAUNCHER" "$G/app" -no-stdlib -classpath "$CP" -d "$G/appbir" >/dev/null 2>&1
emit_il "$G/appil" GeomApp --ref "$G/libil/GeomLib.dll" "$G/appbir"/*.bir.json >/dev/null 2>&1
cp "$G/libil/GeomLib.dll" "$G/appil/"
pkgexpected="$(printf '11\nHi, pkg\nEAST\nString\n4\n25\n52\n52\n10\n7\ndef\nnone')"
pkgactual="$(dotnet "$G/appil/GeomApp.dll" 2>/dev/null)"
if [[ "$pkgactual" == "$pkgexpected" ]]; then
    echo "PASS  roundtrip-pkg (namespace; reified inline; non-local return; properties; ext operator/property; vararg; default arg; nullable)"
else
    echo "FAIL  roundtrip-pkg"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$pkgexpected" "$pkgactual"; exit 1
fi


# ----- GENERIC round-trip, COMBINED with every other round-tripping feature, consumed as Kotlin -----
# Exercises user generics in every POSITION (class type param, member, return, parameter, two type params, generic
# method on a generic class) AND combined with each restored modifier (operator, infix, extension, extension operator,
# top-level suspend, nullable, default arg, vararg). Guards the coordinated fixes:
#   - facadegen: a root-namespace generic open name was `.Box` (leading dot); `Supported`/`CrossType` dropped a generic
#     user type in a signature (`Box<T>` -> Any?) so the whole function vanished.
#   - ilemit: a generic type was named `Box` without the CLR `Box`1` arity (cross-assembly `GetType` missed it); a
#     generic EXTENSION call omitted the `__self` receiver shape; a generic fn with a DEFAULT arg had fewer shapes than
#     the single .NET method's params (now tolerated + default-filled).
#   - injector: `coneOf` lost the method type variable inside `generic:Box:T` (resolved `T` -> Any?, so a returned
#     `Box<T>` became `Box<object>` and crashed at the call site); the generic branch ignored ext receiver / inline /
#     infix / operator / vararg / default-arg overloads (now one unified path).
# (reified generics already worked — a generic method with no carried type. Generic-CLASS member `suspend` is a separate
# pre-existing coroutine×generics limitation that fails the same way WITHOUT round-trip, so it's covered elsewhere.)
GG="$ROOT/build/roundtrip-generic"; rm -rf "$GG"; mkdir -p "$GG/lib" "$GG/app" "$GG/libbir" "$GG/libil" "$GG/appbir" "$GG/appil"
cat > "$GG/lib/lib.kt" <<'EOF'
class Pair2<A, B>(val first: A, val second: B)                       // two type params
class Box<T>(val value: T) {
    fun get(): T = value
    operator fun plus(o: Box<T>): Pair2<T, T> = Pair2(value, o.value) // generic + operator
    infix fun with(o: Box<T>): Pair2<T, T> = Pair2(value, o.value)    // generic + infix
    fun <R> mapTo(f: (T) -> R): R = f(value)                          // generic METHOD on a generic class
}
class Holder<A, B>(val a: A, val b: B) { val label: String get() = "$a/$b" }  // two type params + custom getter
fun <T> wrap(x: T): Box<T> = Box(x)                                  // generic top-level, generic RETURN type
fun <T> unwrap(b: Box<T>): T = b.get()                              // generic top-level, generic PARAM type
fun <T> Box<T>.twice(): Pair2<T, T> = Pair2(value, value)           // generic EXTENSION on a generic type
operator fun <T> Box<T>.times(n: Int): Int = n                      // generic extension OPERATOR
suspend fun <T> echoAsync(x: T): T = x                             // generic + top-level SUSPEND
fun <T> orDefault(x: T?, label: String = "none"): String =         // generic + NULLABLE + DEFAULT arg
    if (x == null) label else x.toString()
fun <T> countAll(vararg xs: T): Int = xs.size                      // generic + VARARG
EOF
cat > "$GG/app/app.kt" <<'EOF'
import kotlinx.coroutines.runBlocking
fun main() {
    val a = Box(3); val b = Box(4)
    println((a + b).first)                    // 3    generic operator +
    println((a with b).second)                // 4    generic infix
    println(Box(5).mapTo { it * 2 })          // 10   generic method on a generic class (+ lambda)
    println(Box(5).get())                     // 5    generic member
    println(Holder(1, "z").label)             // 1/z  two type params + custom getter
    println(wrap(99).get())                   // 99   generic return type
    println(unwrap(Box(8)))                   // 8    generic param type
    println(Box(6).twice().first)             // 6    generic extension on a generic type
    println(Box(6) * 7)                       // 7    generic extension operator
    println(runBlocking { echoAsync("hi") })  // hi   generic top-level suspend
    println(orDefault<String>(null))          // none generic + nullable + default omitted
    println(orDefault("set"))                 // set  default present
    println(countAll(1, 2, 3, 4))             // 4    generic vararg
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$GG/lib" -no-stdlib -classpath "$CP" -d "$GG/libbir" >/dev/null 2>&1
emit_il "$GG/libil" KLib "$GG/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$GG/libil/KLib.dll" --refs "$REFS" >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$GG/k.meta" --refs "$REFS$GG/libil/KLib.dll" Box Pair2 Holder LibKt >/dev/null 2>&1
CLR_TYPES_METADATA="$GG/k.meta" "$LAUNCHER" "$GG/app" -no-stdlib -classpath "$CP" -d "$GG/appbir" >/dev/null 2>&1
emit_il "$GG/appil" KApp --ref "$GG/libil/KLib.dll" "$GG/appbir"/*.bir.json >/dev/null 2>&1
cp "$GG/libil/KLib.dll" "$GG/appil/"
gexpected="$(printf '3\n4\n10\n5\n1/z\n99\n8\n6\n7\nhi\nnone\nset\n4')"
gactual="$(dotnet "$GG/appil/KApp.dll" 2>/dev/null)"
if [[ "$gactual" == "$gexpected" ]]; then
    echo "PASS  roundtrip-generic (user generics in every position × operator/infix/extension/suspend/nullable/default/vararg)"
else
    echo "FAIL  roundtrip-generic"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$gexpected" "$gactual"; exit 1
fi

# ----- HIGHER-ORDER generics: a function-type parameter whose ARG/RETURN is a generic user type (`(Box<U>)->Box<V>`) -----
# The metadata type grammar is recursive (bracketed: `func:[generic:Box[V],generic:Box[U]]`), so a generic user type
# nests inside a lambda parameter — top-level / member / extension / infix / operator / inline all carry it. (Before,
# the flat `func:<ret>:<args>` grammar couldn't nest `generic:` and dropped the whole lambda to `Any?`, killing inference.)
HF="$ROOT/build/roundtrip-generic-hof"; rm -rf "$HF"; mkdir -p "$HF/lib" "$HF/app" "$HF/libbir" "$HF/libil" "$HF/appbir" "$HF/appil"
cat > "$HF/lib/lib.kt" <<'EOF'
class Box<T>(val value: T) { fun get(): T = value }
fun <U, V> apply2(f: (Box<U>) -> Box<V>, x: Box<U>): Box<V> = f(x)        // top-level, lambda arg+ret generic user types
class Wrap<T>(val v: T) { fun <U, V> route(f: (Box<U>) -> Box<V>, x: Box<U>): Box<V> = f(x) }  // member
fun <U, V> Box<U>.mapBox(f: (Box<U>) -> Box<V>): Box<V> = f(this)         // extension
infix fun <U, V> Box<U>.pipe(f: (Box<U>) -> Box<V>): Box<V> = f(this)     // infix extension
operator fun <U, V> Box<U>.times(f: (Box<U>) -> Box<V>): Box<V> = f(this) // operator extension
inline fun <T, U, V, W> Box<T>.alsoMap(f: (Box<U>) -> Box<V>, w: W): Box<W> = Box(w)  // inline + 4 type params
EOF
cat > "$HF/app/app.kt" <<'EOF'
fun main() {
    val inc: (Box<Int>) -> Box<String> = { Box(it.get().toString() + "!") }
    println(apply2(inc, Box(5)).get())                       // 5!
    println(Wrap("w").route(inc, Box(6)).get())              // 6!
    println(Box(7).mapBox(inc).get())                        // 7!
    println((Box(8) pipe inc).get())                         // 8!
    println((Box(9) * inc).get())                            // 9!
    println(Box(1).alsoMap<Int, Int, String, Int>(inc, 42).get())  // 42 (inline ext, explicit type args)
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$HF/lib" -no-stdlib -classpath "$CP" -d "$HF/libbir" >/dev/null 2>&1
emit_il "$HF/libil" KLib "$HF/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$HF/libil/KLib.dll" --refs "$REFS" >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$HF/k.meta" --refs "$REFS$HF/libil/KLib.dll" Box Wrap LibKt >/dev/null 2>&1
CLR_TYPES_METADATA="$HF/k.meta" "$LAUNCHER" "$HF/app" -no-stdlib -classpath "$CP" -d "$HF/appbir" >/dev/null 2>&1
emit_il "$HF/appil" KApp --ref "$HF/libil/KLib.dll" "$HF/appbir"/*.bir.json >/dev/null 2>&1
cp "$HF/libil/KLib.dll" "$HF/appil/"
hfexpected="$(printf '5!\n6!\n7!\n8!\n9!\n42')"
hfactual="$(dotnet "$HF/appil/KApp.dll" 2>/dev/null)"
if [[ "$hfactual" == "$hfexpected" ]]; then
    echo "PASS  roundtrip-generic-hof (generic user types nested in a lambda parameter: top-level/member/extension/infix/operator/inline)"
else
    echo "FAIL  roundtrip-generic-hof"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$hfexpected" "$hfactual"; exit 1
fi

# ----- MEMBER-declared extension functions: `class C { fun T.f() }` consumed via `with(c) { x.f() }` -----
# Covers the cross-product: plain / infix / operator / inline+generic-method / protected, on a generic user receiver.
# Restored via the `,ext` marker (the first param `__self` becomes the extension receiver); the consumer dispatches on
# the enclosing instance with the extension receiver prepended. (Member extension PROPERTIES and SUSPEND member
# extensions are covered by the next section.)
ME="$ROOT/build/roundtrip-memext"; rm -rf "$ME"; mkdir -p "$ME/lib" "$ME/app" "$ME/libbir" "$ME/libil" "$ME/appbir" "$ME/appil"
cat > "$ME/lib/lib.kt" <<'EOF'
class Box<T>(val value: T) { fun get(): T = value }
open class Lib(val k: Int) {
    fun Box<Int>.boost(): Int = get() + k                          // member extension function
    infix fun Box<Int>.glue(o: Box<Int>): Int = get() + o.get() + k // member extension infix
    operator fun Box<Int>.times(n: Int): Int = get() * n + k        // member extension operator
    inline fun <R> Box<Int>.mapped(f: (Int) -> R): R = f(get())     // member extension + inline + generic method + lambda
    protected fun Box<Int>.sshh(): Int = get() * 100 + k           // protected member extension
    fun useProt(b: Box<Int>): Int = b.sshh()                       // protected used internally
}
EOF
cat > "$ME/app/app.kt" <<'EOF'
fun main() {
    val lib = Lib(10)
    with(lib) {
        println(Box(5).boost())            // 15
        println(Box(2) glue Box(3))        // 15
        println(Box(4) * 3)                // 22
        println(Box(7).mapped { it + 1 })  // 8
    }
    println(lib.useProt(Box(1)))           // 110
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$ME/lib" -no-stdlib -classpath "$CP" -d "$ME/libbir" >/dev/null 2>&1
emit_il "$ME/libil" KLib "$ME/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$ME/libil/KLib.dll" --refs "$REFS" >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$ME/k.meta" --refs "$REFS$ME/libil/KLib.dll" Box Lib >/dev/null 2>&1
CLR_TYPES_METADATA="$ME/k.meta" "$LAUNCHER" "$ME/app" -no-stdlib -classpath "$CP" -d "$ME/appbir" >/dev/null 2>&1
emit_il "$ME/appil" KApp --ref "$ME/libil/KLib.dll" "$ME/appbir"/*.bir.json >/dev/null 2>&1
cp "$ME/libil/KLib.dll" "$ME/appil/"
meexpected="$(printf '15\n15\n22\n8\n110')"
meactual="$(dotnet "$ME/appil/KApp.dll" 2>/dev/null)"
if [[ "$meactual" == "$meexpected" ]]; then
    echo "PASS  roundtrip-memext (member extension functions: plain/infix/operator/inline-generic/protected, consumed via with)"
else
    echo "FAIL  roundtrip-memext"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$meexpected" "$meactual"; exit 1
fi

# ----- MEMBER-declared extension PROPERTIES + SUSPEND member extensions -----
# Member extension property (`class C { val T.p }`): restored via a `memextprop` meta line (a `get_p(__self)`/
# `set_p(__self,v)` member method) as a member property with an extension receiver; read/write inside `with(c)` routes
# to C's get_/set_ with the extension receiver prepended. Suspend member extension (`suspend fun T.f()` in a class):
# emitted with the SM nested in C (so it reaches PROTECTED members), exposed via a normal suspend member the consumer
# awaits. Both at public + protected visibility.
MP="$ROOT/build/roundtrip-memext2"; rm -rf "$MP"; mkdir -p "$MP/lib" "$MP/app" "$MP/libbir" "$MP/libil" "$MP/appbir" "$MP/appil"
cat > "$MP/lib/lib.kt" <<'EOF'
class Box<T>(val value: T) { fun get(): T = value }
open class Lib(val k: Int) {
    val Box<Int>.lbl: String get() = "lbl:" + (get() + k)        // member extension property (val)
    var Box<Int>.scaled: Int                                      // member extension property (var)
        get() = get() * k
        set(v) { last = v + k }
    var last: Int = 0
    protected val Box<Int>.secret: Int get() = get() + 1000      // protected member extension property
    fun peek(b: Box<Int>): Int = b.secret
    suspend fun Box<Int>.fetch(): Int = get() + k               // suspend member extension (public)
    protected suspend fun Box<Int>.hidden(): Int = get() * 100 + k  // protected suspend member ext
    suspend fun useFetch(b: Box<Int>): Int = b.fetch()         // exposed via a normal suspend member
    suspend fun useHidden(b: Box<Int>): Int = b.hidden()
}
EOF
cat > "$MP/app/app.kt" <<'EOF'
import kotlinx.coroutines.runBlocking
suspend fun doFetch(lib: Lib, b: Box<Int>): Int = with(lib) { b.fetch() }   // suspend member ext via with() (scope-fn CPS)
suspend fun doHidden(lib: Lib, b: Box<Int>): Int = lib.useHidden(b)
fun main() {
    val lib = Lib(10)
    with(lib) {
        println(Box(7).lbl)       // lbl:17
        println(Box(3).scaled)    // 30
        Box(0).scaled = 5         // last = 15
        println(last)             // 15
    }
    println(lib.peek(Box(2)))                       // 1002 (protected member ext property)
    println(runBlocking { doFetch(lib, Box(5)) })   // 15   (suspend member ext consumed via with(lib){ b.fetch() })
    println(runBlocking { doHidden(lib, Box(2)) })  // 210  (protected suspend member ext via helper)
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$MP/lib" -no-stdlib -classpath "$CP" -d "$MP/libbir" >/dev/null 2>&1
emit_il "$MP/libil" KLib "$MP/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$MP/libil/KLib.dll" --refs "$REFS" >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$MP/k.meta" --refs "$REFS$MP/libil/KLib.dll" Box Lib >/dev/null 2>&1
CLR_TYPES_METADATA="$MP/k.meta" "$LAUNCHER" "$MP/app" -no-stdlib -classpath "$CP" -d "$MP/appbir" >/dev/null 2>&1
emit_il "$MP/appil" KApp --ref "$MP/libil/KLib.dll" "$MP/appbir"/*.bir.json >/dev/null 2>&1
cp "$MP/libil/KLib.dll" "$MP/appil/"
mpexpected="$(printf 'lbl:17\n30\n15\n1002\n15\n210')"
mpactual="$(dotnet "$MP/appil/KApp.dll" 2>/dev/null)"
if [[ "$mpactual" == "$mpexpected" ]]; then
    echo "PASS  roundtrip-memext2 (member extension properties + suspend member extensions, public + protected)"
else
    echo "FAIL  roundtrip-memext2"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$mpexpected" "$mpactual"; exit 1
fi

# ----- DEFAULT ARGUMENTS + NAMED ARGUMENTS: trailing/named-middle/reordered omission, on functions AND constructors -----
# A restored default arg now carries a REAL constant value (`opt:Type=<const>` in the metadata -> a FirLiteralExpression
# applied via replaceDefaultValue), so the consumer can omit it ANYWHERE: trailing, NAMED-MIDDLE (`box(1, c=9)` — skip a
# middle default, provide a later one — which the old @JvmOverloads positional overloads could NOT express), or reordered
# named. Constructors too (`Pt(y=4)`; ilemit now also emits ctor parameter NAMES). String defaults with spaces survive
# (escaped in the token). (.NET BCL methods with an enum/struct default fall back to @JvmOverloads trailing overloads.)
DA="$ROOT/build/roundtrip-defargs"; rm -rf "$DA"; mkdir -p "$DA/lib" "$DA/app" "$DA/libbir" "$DA/libil" "$DA/appbir" "$DA/appil"
cat > "$DA/lib/lib.kt" <<'EOF'
fun greet(name: String, greeting: String = "Hi", punct: String = "!"): String = "$greeting, $name$punct"
fun box(a: Int, b: Int = 2, c: Int = 3): Int = a * 100 + b * 10 + c
fun flags(on: Boolean = true, label: String = "x y"): String = "$on/$label"
class Pt(val x: Int = 0, val y: Int = 0) { override fun toString(): String = "($x,$y)" }
EOF
cat > "$DA/app/app.kt" <<'EOF'
fun main() {
    println(greet("A"))                          // Hi, A!
    println(greet("B", "Yo"))                     // Yo, B!   trailing omit
    println(greet("C", punct = "?"))              // Hi, C?   NAMED MIDDLE omission
    println(greet(greeting = "Hey", name = "E"))  // Hey, E!  reordered named
    println(box(1))                               // 123
    println(box(1, c = 9))                        // 129      NAMED MIDDLE omission
    println(box(a = 5, c = 7))                    // 527      named middle omission
    println(flags())                              // True/x y string default with a space
    println(flags(label = "z"))                   // True/z   named middle omission
    println(Pt(y = 4))                            // (0,4)    ctor named middle omission
    println(Pt(x = 7))                            // (7,0)    ctor named
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$DA/lib" -no-stdlib -classpath "$CP" -d "$DA/libbir" >/dev/null 2>&1
emit_il "$DA/libil" KLib "$DA/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$DA/libil/KLib.dll" --refs "$REFS" >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$DA/k.meta" --refs "$REFS$DA/libil/KLib.dll" Pt LibKt >/dev/null 2>&1
CLR_TYPES_METADATA="$DA/k.meta" "$LAUNCHER" "$DA/app" -no-stdlib -classpath "$CP" -d "$DA/appbir" >/dev/null 2>&1
emit_il "$DA/appil" KApp --ref "$DA/libil/KLib.dll" "$DA/appbir"/*.bir.json >/dev/null 2>&1
cp "$DA/libil/KLib.dll" "$DA/appil/"
daexpected="$(printf 'Hi, A!\nYo, B!\nHi, C?\nHey, E!\n123\n129\n527\nTrue/x y\nTrue/z\n(0,4)\n(7,0)')"
daactual="$(dotnet "$DA/appil/KApp.dll" 2>/dev/null)"
if [[ "$daactual" == "$daexpected" ]]; then
    echo "PASS  roundtrip-defargs (default args: trailing/named-middle/reordered omission, on functions + constructors)"
else
    echo "FAIL  roundtrip-defargs"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$daexpected" "$daactual"; exit 1
fi
