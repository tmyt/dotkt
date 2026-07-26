// KNOWN FAILURE — compiles clean, fails at RUN: prints the WRONG VALUE with no diagnostic at any stage
// (expected 2 / 6 / 2, actual 0 / 0 / 1).
//
// A local `fun` that WRITES a captured enclosing `var` silently loses the write. kotc lifts a local fun to a static
// method whose captures are BY-VALUE leading parameters (BirEmitterLifts.liftLocalFn), and the ref-cell analysis
// (BirEmitter.computeRefCells) does not treat a local fun as a capture boundary, so the `var` stays a plain local: the
// write lands on the lifted method's own parameter and the caller never sees it.
//
// The lambda, object-expression and local-class boundaries ARE celled at every emission root
// (tests/basic/fixtures/CapturedVarRefCellTests.kt), and a local fun correctly rides a cell one of THOSE created for
// the same `var` — only a `var` written exclusively by a local fun is lost.
//
// Making a local fun a boundary needs a bir2cir fix as well: it cannot resolve a cell whose element type is an
// enclosing TYPE VARIABLE inside a file-class static method. A lifted local fn is a file-class static that RE-DECLARES
// the enclosing free type params as its own, while the body's `tv` tokens keep their original scope/index, and
// SharedSyntheticSynthesis resolves a cell's element positionally against the declaring method's own type params with
// no type-scope pool for a file method. `GenericBox` below compiles today (uncelled, wrong value); celling it aborts
// the build instead:
//   bir2cir: ref-cell `dotkt$appKt$Ref$__t___tv___scope___type___i__0_` cannot resolve type type variable #0
//            in its lexical owner
// The lambda/object boundaries avoid this because their cell lives in a lifted CLASS, which carries the type params.
//
// The second blocker — captures are not propagated transitively through a CALL to a lifted local fun — has its own
// repro next door: tests/known-fail/localfun-capture-write-via-closure/.

fun viaLocalFun(): Int {
    var n = 0
    fun bump() { n++ }
    bump(); bump()
    return n                      // expected 2, actual 0
}

class InInitBlock {
    val total: Int
    init {
        var n = 0
        fun bump() { n += 3 }
        bump(); bump()
        total = n                 // expected 6, actual 0
    }
}

class GenericBox<T>(val t: T, val other: T) {
    fun run(): T {
        var cur = t
        fun set() { cur = other }
        set()
        return cur                // expected 2 (`other`), actual 1 (`t`)
    }
}

fun main() {
    println(viaLocalFun())
    println(InInitBlock().total)
    println(GenericBox(1, 2).run())
}
