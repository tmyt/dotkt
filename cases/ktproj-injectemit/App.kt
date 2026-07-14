// #15 EMIT-HALF regression: `demo.Plain`/`demo.hello` are declared in this app's OWN compile (the recursive
// `**/*.kt` glob pulls in the nested lib/Demo.kt) AND exported by the referenced Demo.dll (<ProjectReference>).
// The FRONTEND "source wins" fix (issue #15 core) suppresses the injected copy so the SOURCE declaration
// compiles into a LOCAL BIR type. bir2cir must then PREFER that local type over the referenced dll of the same
// FQN — emitting a local `new demo.Plain` (this-assembly-emitted), NOT a `newClr` against Demo.dll (which would
// make the app both emit `demo.Plain` locally AND `newClr` the ref's copy → ilemit conflict). Same local-over-ref
// precedence for the top-level `hello()` call. Before the fix: bir2cir/ilemit error. After: runs 42 / plain.
import demo.hello
import demo.Plain

fun main() {
    println(hello())     // 42     — the local top-level fun, not the referenced dll's copy
    val p = Plain()      // local `new demo.Plain`, not `newClr` against Demo.dll
    println(p.tag)       // plain
}
