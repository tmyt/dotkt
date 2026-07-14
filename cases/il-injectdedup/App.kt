// #15 regression: `demo.Plain` (a bare class) and `demo.hello` (a bare top-level fun) are BOTH declared in
// this compile's SOURCE (Demo.kt) AND listed in the facadegen injection metadata (demo.meta, as if injected
// from a <ProjectReference>'d dll that exports the same identities). Before the fix the injector materialized
// a SECOND, identical copy of each → `overload resolution ambiguity` here + `conflicting overloads/
// declarations` at Demo.kt. The source declaration must WIN and the injected copy be suppressed.
import demo.hello
import demo.Plain

fun main() {
    println(hello())     // 42  — the source top-level fun, not a doubled injected overload
    val p = Plain()      // the source ctor, not a doubled injected ctor
    println(p.tag)       // plain
}
