#!/usr/bin/env bash
# DotKt round-trip: a Kotlin assembly compiled by DotKt, consumed AS KOTLIN by another module — the Kotlin
# modifiers with no .NET analog (infix / operator / suspend / top-level) survive the trip. They're stamped onto the
# emitted IL as DotKt.Metadata attributes ([KotlinFunction]/[KotlinFile]) by ilemit, then read back by facadegen
# (--meta) and restored on the synthesized FIR by ClrTypeInjection. This is the basis of consuming compiled Kotlin
# libraries (kotlinx-*) as Kotlin. See docs/design-kotlin-metadata-attributes.md.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STDLIB="$(find "$HOME/.gradle/caches" -name 'kotlin-stdlib-2.2.0.jar' | head -1)"
CORO="$(find "$HOME/.gradle/caches" -name 'kotlinx-coroutines-core-jvm-1.8.0.jar' | head -1)"
CP="$STDLIB:$CORO"

# Build the toolchain (compiler launcher + ilemit + facadegen + retarget + runtime) once.
"$ROOT/gradlew" -q :compiler:installDist >/dev/null 2>&1
LAUNCHER="$ROOT/compiler/build/install/compiler/bin/compiler"
dotnet build "$ROOT/tools/ilemit"        -c Release -o "$ROOT/build/ilemit-bin"     -v q --nologo >/dev/null
dotnet build "$ROOT/tools/facadegen"     -c Release -o "$ROOT/build/facadegen-bin"  -v q --nologo >/dev/null
dotnet build "$ROOT/tools/retarget"      -c Release -o "$ROOT/build/retarget-bin"   -v q --nologo >/dev/null
dotnet build "$ROOT/runtime/DotKt.Runtime" -c Release -o "$ROOT/build/dotkt-runtime" -v q --nologo >/dev/null 2>&1
DOTKT_RT="$ROOT/build/dotkt-runtime/DotKt.Runtime.dll"
REFPACK="$(ls -d /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net10.0 2>/dev/null | sort -V | tail -1)"
REFS="$(ls "$REFPACK"/*.dll | tr '\n' ';')"

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

# 1. compile + emit + retarget the library (the emit stamps [KotlinFunction]/[KotlinFile]).
CLR_TYPES_METADATA="" "$LAUNCHER" "$R/lib" -no-stdlib -classpath "$CP" -d "$R/libbir" >/dev/null 2>&1
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$R/libil" KLib --ref "$DOTKT_RT" "$R/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$R/libil/KLib.dll" --refs "$REFS$DOTKT_RT" >/dev/null 2>&1
# 2. facadegen --meta reads the attributes back into the injection metadata.
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$R/k.meta" --refs "$REFS$R/libil/KLib.dll;$DOTKT_RT" Vec LibKt >/dev/null 2>&1
# 3. compile the consumer WITH the metadata (the injector restores infix/operator/suspend/top-level on FIR).
CLR_TYPES_METADATA="$R/k.meta" "$LAUNCHER" "$R/app" -no-stdlib -classpath "$CP" -d "$R/appbir" >/dev/null 2>&1
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$R/appil" KApp --ref "$R/libil/KLib.dll" --ref "$DOTKT_RT" "$R/appbir"/*.bir.json >/dev/null 2>&1
cp "$R/libil/KLib.dll" "$DOTKT_RT" "$R/appil/"

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
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$G/libil" GeomLib --ref "$DOTKT_RT" "$G/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$G/libil/GeomLib.dll" --refs "$REFS$DOTKT_RT" >/dev/null 2>&1
"$LAUNCHER" --scan-imports --output "$G/imports.txt" "$G/app"/*.kt >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$G/meta" --refs "$REFS$G/libil/GeomLib.dll;$DOTKT_RT" --import-list "$G/imports.txt" >/dev/null 2>&1
CLR_TYPES_METADATA="$G/meta" "$LAUNCHER" "$G/app" -no-stdlib -classpath "$CP" -d "$G/appbir" >/dev/null 2>&1
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$G/appil" GeomApp --ref "$G/libil/GeomLib.dll" --ref "$DOTKT_RT" "$G/appbir"/*.bir.json >/dev/null 2>&1
cp "$G/libil/GeomLib.dll" "$DOTKT_RT" "$G/appil/"
pkgexpected="$(printf '11\nHi, pkg\nEAST\nString\n4\n25\n52\n52\n10\n7\ndef\nnone')"
pkgactual="$(dotnet "$G/appil/GeomApp.dll" 2>/dev/null)"
if [[ "$pkgactual" == "$pkgexpected" ]]; then
    echo "PASS  roundtrip-pkg (namespace; reified inline; non-local return; properties; ext operator/property; vararg; default arg; nullable)"
else
    echo "FAIL  roundtrip-pkg"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$pkgexpected" "$pkgactual"; exit 1
fi

# ----- NAMESPACE PROJECTION: a library in .NET namespace `dotktx.foo` is consumed via idiomatic `import kotlinx.foo.*` -----
# `[assembly: DotKtNamespaceProjection("kotlinx.foo","dotktx.foo")]` (stamped by `ilemit --ns-projection`) declares the map;
# the consumer's facadegen resolves the import through it and the injector exposes the types under the Kotlin package.
N="$ROOT/build/roundtrip-nsproj"; rm -rf "$N"; mkdir -p "$N/lib" "$N/app" "$N/libbir" "$N/libil" "$N/appbir" "$N/appil"
cat > "$N/lib/lib.kt" <<'EOF'
package dotktx.foo
class Greeter(val name: String) { fun greet(): String = "Hello, " + name }
fun hello(): String = "hi from foo"
EOF
cat > "$N/app/app.kt" <<'EOF'
import kotlinx.foo.*
fun main() { println(Greeter("Bob").greet()); println(hello()) }
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$N/lib" -no-stdlib -classpath "$CP" -d "$N/libbir" >/dev/null 2>&1
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$N/libil" FooLib --ref "$DOTKT_RT" --ns-projection kotlinx.foo=dotktx.foo "$N/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$N/libil/FooLib.dll" --refs "$REFS$DOTKT_RT" >/dev/null 2>&1
"$LAUNCHER" --scan-imports --output "$N/imports.txt" "$N/app"/*.kt >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$N/meta" --refs "$REFS$N/libil/FooLib.dll;$DOTKT_RT" --import-list "$N/imports.txt" >/dev/null 2>&1
CLR_TYPES_METADATA="$N/meta" "$LAUNCHER" "$N/app" -no-stdlib -classpath "$CP" -d "$N/appbir" >/dev/null 2>&1
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$N/appil" FooApp --ref "$N/libil/FooLib.dll" --ref "$DOTKT_RT" "$N/appbir"/*.bir.json >/dev/null 2>&1
cp "$N/libil/FooLib.dll" "$DOTKT_RT" "$N/appil/"
nsexpected="$(printf 'Hello, Bob\nhi from foo')"
nsactual="$(dotnet "$N/appil/FooApp.dll" 2>/dev/null)"
if [[ "$nsactual" == "$nsexpected" ]]; then
    echo "PASS  roundtrip-nsproj (DotKtNamespaceProjection: import kotlinx.foo.* resolves a library living in .NET namespace dotktx.foo)"
else
    echo "FAIL  roundtrip-nsproj"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$nsexpected" "$nsactual"; exit 1
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
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$GG/libil" KLib --ref "$DOTKT_RT" "$GG/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$GG/libil/KLib.dll" --refs "$REFS$DOTKT_RT" >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$GG/k.meta" --refs "$REFS$GG/libil/KLib.dll;$DOTKT_RT" Box Pair2 Holder LibKt >/dev/null 2>&1
CLR_TYPES_METADATA="$GG/k.meta" "$LAUNCHER" "$GG/app" -no-stdlib -classpath "$CP" -d "$GG/appbir" >/dev/null 2>&1
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$GG/appil" KApp --ref "$GG/libil/KLib.dll" --ref "$DOTKT_RT" "$GG/appbir"/*.bir.json >/dev/null 2>&1
cp "$GG/libil/KLib.dll" "$DOTKT_RT" "$GG/appil/"
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
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$HF/libil" KLib --ref "$DOTKT_RT" "$HF/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$HF/libil/KLib.dll" --refs "$REFS$DOTKT_RT" >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$HF/k.meta" --refs "$REFS$HF/libil/KLib.dll;$DOTKT_RT" Box Wrap LibKt >/dev/null 2>&1
CLR_TYPES_METADATA="$HF/k.meta" "$LAUNCHER" "$HF/app" -no-stdlib -classpath "$CP" -d "$HF/appbir" >/dev/null 2>&1
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$HF/appil" KApp --ref "$HF/libil/KLib.dll" --ref "$DOTKT_RT" "$HF/appbir"/*.bir.json >/dev/null 2>&1
cp "$HF/libil/KLib.dll" "$DOTKT_RT" "$HF/appil/"
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
# the enclosing instance with the extension receiver prepended. (A member extension PROPERTY and a SUSPEND member
# extension are rejected with a source-located compile error — see docs/future-work-interop.md.)
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
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$ME/libil" KLib --ref "$DOTKT_RT" "$ME/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$ME/libil/KLib.dll" --refs "$REFS$DOTKT_RT" >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$ME/k.meta" --refs "$REFS$ME/libil/KLib.dll;$DOTKT_RT" Box Lib >/dev/null 2>&1
CLR_TYPES_METADATA="$ME/k.meta" "$LAUNCHER" "$ME/app" -no-stdlib -classpath "$CP" -d "$ME/appbir" >/dev/null 2>&1
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$ME/appil" KApp --ref "$ME/libil/KLib.dll" --ref "$DOTKT_RT" "$ME/appbir"/*.bir.json >/dev/null 2>&1
cp "$ME/libil/KLib.dll" "$DOTKT_RT" "$ME/appil/"
meexpected="$(printf '15\n15\n22\n8\n110')"
meactual="$(dotnet "$ME/appil/KApp.dll" 2>/dev/null)"
if [[ "$meactual" == "$meexpected" ]]; then
    echo "PASS  roundtrip-memext (member extension functions: plain/infix/operator/inline-generic/protected, consumed via with)"
else
    echo "FAIL  roundtrip-memext"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$meexpected" "$meactual"; exit 1
fi
