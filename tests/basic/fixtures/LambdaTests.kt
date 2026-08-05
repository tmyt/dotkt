// Lambda / closure / higher-order / function-reference battery — migrates the lambda/closure/HOF/funref family of
// cases/il-* onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute
// method whose per-value assertEquals is strictly stronger (typed) and self-documenting. Every value the old il_check
// asserted is preserved 1:1 (see the `// <expected>` comments). Ordered side-effecting `println`s become a captured
// log list asserted in order — the closure/HOF STRUCTURE that was the actual subject is preserved unchanged.
//
// Multi-file subject preserved: il-mfclosure / il-mflambda existed to prove per-file synthetic-type (closure class /
// lifted lambda) naming does NOT collide across files in one linked assembly. Their "other file" decls live in the
// SIBLING file LambdaCrossFileSupport.kt (same assembly), so the closures/lambdas are lifted into a DIFFERENT file class —
// exactly the two-file collision shape the cases guarded.
//
// Coverage preserved (old case -> method):
//   il-closure      -> closure_capturingAdder        capturing lambda -> closure class field (captured `base`)
//   il-lambda       -> lambda_higherOrder            non-capturing lambda -> delegate; HOF/function-type params
//   il-genclosure   -> genclosure_capturesT          closure in a generic fn capturing T-typed values (generic closure class)
//   il-genhof       -> genhof_genericForEach         generic fn iterating List<T> applying (T)->Unit (TypeBuilderInstantiation)
//   il-mfclosure    -> mfclosure_multiFile           two files each emit a capturing closure + ref cell (no synthetic collision)
//   il-mflambda     -> mflambda_multiFile            two files each lift their own lambdas (per-file lifted state resets)
//   il-writecapture -> writecapture_localClassObject #68 local class / object expr WRITES a captured outer `var` (shared ref cell)
//                      — the FUNCTION-BODY root; the constructor/initializer/accessor/static-initializer roots of the
//                        same #68 subject live in CapturedVarRefCellTests.kt
//   il-funref       -> funref_callableReferences     `::foo` / `obj::m` / `Class::m` callable refs -> delegate
//   il-extfunref    -> extfunref_extensionReferences unbound `Type::extensionReferenceFn` refs (stdlib + same-module) -> static forwarder
//
// Top-level names use feature stems (`closure`, `lambda`, `genericClosure`, `genericHigherOrder`,
// `multiFileClosure`, `multiFileLambda`, `writeCapture`, `functionReference`, and `extensionReference`) so they
// remain readable and assembly-unique.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

// ---- il-closure : capturing lambda (closure) — captured `base` becomes a closure-class field --------------------
fun closureMakeAdder(base: Int): (Int) -> Int = { x -> x + base }
fun closureApplyN(f: (Int) -> Int, n: Int): Int = f(n)

// ---- il-lambda : lambda -> delegate (non-capturing), higher-order functions, function-type params ---------------
fun lambdaApply2(f: (Int) -> Int, x: Int): Int = f(x)
fun lambdaTwice(g: (Int) -> Int, x: Int): Int = g(g(x))

// ---- il-genclosure : closures in a generic fn CAPTURING T-typed values (synthesized closure class generic over T) -
fun <T> genericClosureCapVal(x: T, log: MutableList<String>) { val run = { log.add(x.toString()) }; run() }
fun <T> genericClosureCapFn(f: (T) -> Unit, x: T) { val run = { f(x) }; run() }
fun <T> genericClosureCapList(xs: List<T>, log: MutableList<String>) { val run = { for (e in xs) log.add(e.toString()) }; run() }
fun <T> genericClosureCapRet(x: T): T { val run = { x }; return run() }
// A LOCAL FUNCTION capturing T is lifted to a generic static method (same root cause as the closure class).
fun <T> genericClosureCapLocalFn(x: T): T {
    fun inner(): T { return x }
    return inner()
}

// ---- il-genhof : generic fn iterating List<T> applying a (T)->Unit lambda (TypeBuilderInstantiation.GetMethod) ----
fun <T> genericHigherOrderEach(xs: List<T>, f: (T) -> Unit) { for (x in xs) f(x) }

// ---- il-mfclosure : file-A half — a capturing closure + ref cell for the captured `var` (other half in ...B.kt) --
fun multiFileClosureApplyA(f: () -> Int): Int = f()
fun multiFileClosureFromA(): Int { var flag = false; return multiFileClosureApplyA({ flag = true; if (flag) 10 else 0 }) }

// ---- il-mflambda : file-A half — lifts its own lambdas into THIS file class (other half in LambdaCrossFileSupport.kt) -------
fun multiFileLambdaRunA(f: () -> Unit) { f() }
fun multiFileLambdaFromA(log: MutableList<String>) { multiFileLambdaRunA { log.add("A1") }; multiFileLambdaRunA { log.add("A2") } }

// ---- il-writecapture : #68 local class / object expr WRITES a captured outer `var` (shared heap ref-cell) --------
fun writeCaptureCounterViaClass(): Int {
    var n = 0
    class Bump { fun go() { n++ } }
    val b = Bump()
    b.go(); b.go(); b.go()
    return n
}
fun writeCaptureCounterViaObject(): Int {
    var m = 10
    val o = object { fun go() { m += 5 } }
    o.go(); o.go()
    return m
}

// ---- il-funref : callable references (`::foo`, `obj::m`, `Class::m`) -> a delegate -------------------------------
fun functionReferenceIsEven(n: Int): Boolean = n % 2 == 0
fun functionReferenceSquare(n: Int): Int = n * n
fun functionReferenceGreet(name: String): String = "Hi, $name"
fun functionReferenceRender(n: Int): String = "n=$n"
fun <T> functionReferenceJoinRendered(xs: List<T>, render: (T) -> String): String =
    xs.joinToString(",", transform = render)
class FunctionReferenceCalc(val base: Int) {
    fun addTo(x: Int): Int = base + x
    open fun label(): String = "calc$base"
}
fun functionReferenceApply2(f: (Int) -> Int, v: Int): Int = f(v)
fun functionReferenceApplyTo(f: (FunctionReferenceCalc, Int) -> Int, c: FunctionReferenceCalc, v: Int): Int = f(c, v)

// ---- il-extfunref : unbound `Type::extensionReferenceFn` refs (stdlib `isNotBlank` + same-module) -> static forwarder ----------
fun String.extensionReferenceShout(): String = uppercase() + "!"
fun String.extensionReferenceDoubleLen(): Int = length * 2
fun String.extensionReferenceRepeatBy(n: Int): String = repeat(n)
fun String.extensionReferenceLogTo(sb: StringBuilder): Unit { sb.append("[").append(this).append("]") }

class LambdaTests {
    @TestAttribute
    fun capturingAdder() {
        val add10 = closureMakeAdder(10)
        val add100 = closureMakeAdder(100)
        assertEquals(15, closureApplyN(add10, 5))    // 15
        assertEquals(105, closureApplyN(add100, 5))  // 105
        assertEquals(17, add10(7))               // 17
    }

    @TestAttribute
    fun higherOrder() {
        assertEquals(42, lambdaApply2({ n -> n * 2 }, 21))  // 42
        assertEquals(12, lambdaTwice({ n -> n + 1 }, 10))   // 12
    }

    @TestAttribute
    fun capturesT() {
        val log = mutableListOf<String>()
        genericClosureCapVal(1, log)                        // 1
        genericClosureCapFn({ y -> log.add("fn:$y") }, 2)   // fn:2
        genericClosureCapList(listOf(3, 4), log)            // 3 / 4
        log.add("ret:" + genericClosureCapRet(5))           // ret:5
        log.add("lf:" + genericClosureCapLocalFn(6))        // lf:6
        assertEquals("1|fn:2|3|4|ret:5|lf:6", log.joinToString("|"))
    }

    @TestAttribute
    fun genericForEach() {
        val log = mutableListOf<Int>()
        genericHigherOrderEach(listOf(1, 2, 3)) { log.add(it) }
        assertEquals("1|2|3", log.joinToString("|"))  // 1 / 2 / 3
    }

    @TestAttribute
    fun multiFileClosureState() {
        // fromA (this file) and fromB (LambdaCrossFileSupport.kt) each emit a capturing closure + a ref cell for a captured
        // `var flag` — with per-file synthetic names these would collide in the one linked assembly.
        assertEquals(10, multiFileClosureFromA())  // 10
        assertEquals(20, multiFileClosureFromB())  // 20
    }

    @TestAttribute
    fun multiFileLambdaState() {
        // fromA's lambdas lift into THIS file's class; runB's lambda lifts into LambdaCrossFileSupport.kt's class — the
        // per-file lifted-lambda state must reset so file A's lambdas don't leak into file B's class.
        val log = mutableListOf<String>()
        multiFileLambdaFromA(log)               // A1 / A2
        multiFileLambdaRunB { log.add("B1") }   // B1
        assertEquals("A1|A2|B1", log.joinToString("|"))
    }

    @TestAttribute
    fun localClassObject() {
        assertEquals(3, writeCaptureCounterViaClass())    // 3
        assertEquals(20, writeCaptureCounterViaObject())  // 20
    }

    @TestAttribute
    fun callableReferences() {
        val xs = listOf(1, 2, 3, 4, 5, 6)
        assertEquals("2,4,6", xs.filter(::functionReferenceIsEven).joinToString(","))            // 2,4,6
        assertEquals("1,4,9,16,25,36", xs.map(::functionReferenceSquare).joinToString(","))      // 1,4,9,16,25,36
        val f: (String) -> String = ::functionReferenceGreet
        assertEquals("Hi, Kotlin", f("Kotlin"))                                       // Hi, Kotlin
        val c = FunctionReferenceCalc(100)
        val bound = c::addTo                                                          // bound instance ref
        assertEquals(105, bound(5))                                                   // 105
        assertEquals(107, functionReferenceApply2(c::addTo, 7))                                  // 107
        val lbl: () -> String = c::label                                             // bound ref to an open method (ldvirtftn)
        assertEquals("calc100", lbl())                                               // calc100
        val unb = FunctionReferenceCalc::addTo                                                   // unbound (FunctionReferenceCalc, Int) -> Int
        assertEquals(203, unb(FunctionReferenceCalc(200), 3))                                    // 203
        assertEquals(42, functionReferenceApplyTo(FunctionReferenceCalc::addTo, FunctionReferenceCalc(40), 2))         // 42
        // #190: a non-literal `(Int) -> String` must be adapted at the call boundary when
        // `joinToString` requires `(Int) -> CharSequence`; both stored and callable-reference values.
        val render: (Int) -> String = { n -> "v=$n" }
        assertEquals("v=1,v=2,v=3", listOf(1, 2, 3).joinToString(",", transform = render))
        assertEquals("n=4,n=5", listOf(4, 5).joinToString(",", transform = ::functionReferenceRender))
        assertEquals("g=6,g=7", functionReferenceJoinRendered(listOf(6, 7)) { n -> "g=$n" })
    }

    @TestAttribute
    fun extensionReferences() {
        val lines = listOf("  hi ", "   ", "world", "")
        // unbound cross-module stdlib extension ref passed to a HOF (the Indent.kt case)
        assertEquals("  hi |world", lines.filter(String::isNotBlank).joinToString("|"))  // "  hi |world"
        val words = listOf("a", "hey", "hello")
        assertEquals("2,6,10", words.map(String::extensionReferenceDoubleLen).joinToString(","))        // 2,6,10
        val f: (String) -> String = String::extensionReferenceShout
        assertEquals("KOTLIN!", f("kotlin"))                                            // KOTLIN!
        val g: (String, Int) -> String = String::extensionReferenceRepeatBy
        assertEquals("ababab", g("ab", 3))                                              // ababab
        val sb = StringBuilder()
        val h: (String, StringBuilder) -> Unit = String::extensionReferenceLogTo
        h("a", sb); h("b", sb)
        assertEquals("[a][b]", sb.toString())                                           // [a][b]
    }

}
