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

# ----- GENERIC round-trip: a generic user CLASS + non-reified generic TOP-LEVEL functions, consumed as Kotlin -----
# Guards two coordinated fixes: (1) facadegen emitted a root-namespace generic type's open .NET name as `.Box` (a
# leading dot — `t.Namespace` is null at the root), unresolvable by the consumer; (2) ilemit named the generic type
# `Box` without the CLR `1` arity suffix, so a cross-assembly `GetType("Box`1")` missed it (same-assembly use went
# through the `_types` registry by BIR name, so it never showed). reified generics already worked (a generic method,
# no carried type) — this covers the non-reified user class + plain generic top-level functions.
GG="$ROOT/build/roundtrip-generic"; rm -rf "$GG"; mkdir -p "$GG/lib" "$GG/app" "$GG/libbir" "$GG/libil" "$GG/appbir" "$GG/appil"
cat > "$GG/lib/lib.kt" <<'EOF'
class Box<T>(val value: T) {
    fun get(): T = value
}
fun <T> identity(x: T): T = x
fun <T> firstOf(a: T, b: T): T = a
EOF
cat > "$GG/app/app.kt" <<'EOF'
fun main() {
    println(Box<Int>(42).get())   // generic user class, constructed + member call
    println(identity("hello"))    // non-reified generic top-level fun
    println(firstOf(1, 2))        // generic top-level fun, inferred type arg
}
EOF
CLR_TYPES_METADATA="" "$LAUNCHER" "$GG/lib" -no-stdlib -classpath "$CP" -d "$GG/libbir" >/dev/null 2>&1
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$GG/libil" KLib --ref "$DOTKT_RT" "$GG/libbir"/*.bir.json >/dev/null 2>&1
dotnet "$ROOT/build/retarget-bin/retarget.dll" "$GG/libil/KLib.dll" --refs "$REFS$DOTKT_RT" >/dev/null 2>&1
dotnet "$ROOT/build/facadegen-bin/facadegen.dll" --meta "$GG/k.meta" --refs "$REFS$GG/libil/KLib.dll;$DOTKT_RT" Box LibKt >/dev/null 2>&1
CLR_TYPES_METADATA="$GG/k.meta" "$LAUNCHER" "$GG/app" -no-stdlib -classpath "$CP" -d "$GG/appbir" >/dev/null 2>&1
dotnet "$ROOT/build/ilemit-bin/ilemit.dll" "$GG/appil" KApp --ref "$GG/libil/KLib.dll" --ref "$DOTKT_RT" "$GG/appbir"/*.bir.json >/dev/null 2>&1
cp "$GG/libil/KLib.dll" "$DOTKT_RT" "$GG/appil/"
gexpected="$(printf '42\nhello\n1')"
gactual="$(dotnet "$GG/appil/KApp.dll" 2>/dev/null)"
if [[ "$gactual" == "$gexpected" ]]; then
    echo "PASS  roundtrip-generic (generic user class + non-reified generic top-level functions consumed as Kotlin)"
else
    echo "FAIL  roundtrip-generic"; printf -- '--- expected ---\n%s\n--- actual ---\n%s\n' "$gexpected" "$gactual"; exit 1
fi
