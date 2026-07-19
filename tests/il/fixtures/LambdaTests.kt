// Lambda / closure / higher-order / function-reference battery — migrates the lambda/closure/HOF/funref family of
// cases/il-* onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute
// method whose per-value assertEquals is strictly stronger (typed) and self-documenting. Every value the old il_check
// asserted is preserved 1:1 (see the `// <expected>` comments). Ordered side-effecting `println`s become a captured
// log list asserted in order — the closure/HOF STRUCTURE that was the actual subject is preserved unchanged.
//
// Multi-file subject preserved: il-mfclosure / il-mflambda existed to prove per-file synthetic-type (closure class /
// lifted lambda) naming does NOT collide across files in one linked assembly. Their "other file" decls live in the
// SIBLING file LambdaTestsB.kt (same assembly), so the closures/lambdas are lifted into a DIFFERENT file class —
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
//   il-funref       -> funref_callableReferences     `::foo` / `obj::m` / `Class::m` callable refs -> delegate
//   il-extfunref    -> extfunref_extensionReferences unbound `Type::extFn` refs (stdlib + same-module) -> static forwarder
//   il-threadlambda -> threadlambda_delegateOverload #19 bare no-arrow `{ }` binds the preferred delegate overload (ThreadStart/Action)
//
// Top-level names are unique within this single battery assembly (one project = one namespace) and family-prefixed
// (`clo`/`lam`/`genclo`/`genhof`/`mfc`/`mfl`/`wc`/`funref`/`ext`) to avoid clashing with sibling batteries and stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Thread
import System.Threading.Tasks.Task

// ---- il-closure : capturing lambda (closure) — captured `base` becomes a closure-class field --------------------
fun cloMakeAdder(base: Int): (Int) -> Int = { x -> x + base }
fun cloApplyN(f: (Int) -> Int, n: Int): Int = f(n)

// ---- il-lambda : lambda -> delegate (non-capturing), higher-order functions, function-type params ---------------
fun lamApply2(f: (Int) -> Int, x: Int): Int = f(x)
fun lamTwice(g: (Int) -> Int, x: Int): Int = g(g(x))

// ---- il-genclosure : closures in a generic fn CAPTURING T-typed values (synthesized closure class generic over T) -
fun <T> gencloCapVal(x: T, log: MutableList<String>) { val run = { log.add(x.toString()) }; run() }
fun <T> gencloCapFn(f: (T) -> Unit, x: T) { val run = { f(x) }; run() }
fun <T> gencloCapList(xs: List<T>, log: MutableList<String>) { val run = { for (e in xs) log.add(e.toString()) }; run() }
fun <T> gencloCapRet(x: T): T { val run = { x }; return run() }
// A LOCAL FUNCTION capturing T is lifted to a generic static method (same root cause as the closure class).
fun <T> gencloCapLocalFn(x: T): T {
    fun inner(): T { return x }
    return inner()
}

// ---- il-genhof : generic fn iterating List<T> applying a (T)->Unit lambda (TypeBuilderInstantiation.GetMethod) ----
fun <T> genhofEach(xs: List<T>, f: (T) -> Unit) { for (x in xs) f(x) }

// ---- il-mfclosure : file-A half — a capturing closure + ref cell for the captured `var` (other half in ...B.kt) --
fun mfcApplyA(f: () -> Int): Int = f()
fun mfcFromA(): Int { var flag = false; return mfcApplyA({ flag = true; if (flag) 10 else 0 }) }

// ---- il-mflambda : file-A half — lifts its own lambdas into THIS file class (other half in LambdaTestsB.kt) -------
fun mflRunA(f: () -> Unit) { f() }
fun mflFromA(log: MutableList<String>) { mflRunA { log.add("A1") }; mflRunA { log.add("A2") } }

// ---- il-writecapture : #68 local class / object expr WRITES a captured outer `var` (shared heap ref-cell) --------
fun wcCounterViaClass(): Int {
    var n = 0
    class Bump { fun go() { n++ } }
    val b = Bump()
    b.go(); b.go(); b.go()
    return n
}
fun wcCounterViaObject(): Int {
    var m = 10
    val o = object { fun go() { m += 5 } }
    o.go(); o.go()
    return m
}

// ---- il-funref : callable references (`::foo`, `obj::m`, `Class::m`) -> a delegate -------------------------------
fun funrefIsEven(n: Int): Boolean = n % 2 == 0
fun funrefSquare(n: Int): Int = n * n
fun funrefGreet(name: String): String = "Hi, $name"
class FunrefCalc(val base: Int) {
    fun addTo(x: Int): Int = base + x
    open fun label(): String = "calc$base"
}
fun funrefApply2(f: (Int) -> Int, v: Int): Int = f(v)
fun funrefApplyTo(f: (FunrefCalc, Int) -> Int, c: FunrefCalc, v: Int): Int = f(c, v)

// ---- il-extfunref : unbound `Type::extFn` refs (stdlib `isNotBlank` + same-module) -> static forwarder ----------
fun String.extShout(): String = uppercase() + "!"
fun String.extDoubleLen(): Int = length * 2
fun String.extRepeatBy(n: Int): String = repeat(n)
fun String.extLogTo(sb: StringBuilder): Unit { sb.append("[").append(this).append("]") }

class LambdaTests {
    @TestAttribute
    fun closure_capturingAdder() {
        val add10 = cloMakeAdder(10)
        val add100 = cloMakeAdder(100)
        assertEquals(15, cloApplyN(add10, 5))    // 15
        assertEquals(105, cloApplyN(add100, 5))  // 105
        assertEquals(17, add10(7))               // 17
    }

    @TestAttribute
    fun lambda_higherOrder() {
        assertEquals(42, lamApply2({ n -> n * 2 }, 21))  // 42
        assertEquals(12, lamTwice({ n -> n + 1 }, 10))   // 12
    }

    @TestAttribute
    fun genclosure_capturesT() {
        val log = mutableListOf<String>()
        gencloCapVal(1, log)                        // 1
        gencloCapFn({ y -> log.add("fn:$y") }, 2)   // fn:2
        gencloCapList(listOf(3, 4), log)            // 3 / 4
        log.add("ret:" + gencloCapRet(5))           // ret:5
        log.add("lf:" + gencloCapLocalFn(6))        // lf:6
        assertEquals("1|fn:2|3|4|ret:5|lf:6", log.joinToString("|"))
    }

    @TestAttribute
    fun genhof_genericForEach() {
        val log = mutableListOf<Int>()
        genhofEach(listOf(1, 2, 3)) { log.add(it) }
        assertEquals("1|2|3", log.joinToString("|"))  // 1 / 2 / 3
    }

    @TestAttribute
    fun mfclosure_multiFile() {
        // fromA (this file) and fromB (LambdaTestsB.kt) each emit a capturing closure + a ref cell for a captured
        // `var flag` — with per-file synthetic names these would collide in the one linked assembly.
        assertEquals(10, mfcFromA())  // 10
        assertEquals(20, mfcFromB())  // 20
    }

    @TestAttribute
    fun mflambda_multiFile() {
        // fromA's lambdas lift into THIS file's class; runB's lambda lifts into LambdaTestsB.kt's class — the
        // per-file lifted-lambda state must reset so file A's lambdas don't leak into file B's class.
        val log = mutableListOf<String>()
        mflFromA(log)               // A1 / A2
        mflRunB { log.add("B1") }   // B1
        assertEquals("A1|A2|B1", log.joinToString("|"))
    }

    @TestAttribute
    fun writecapture_localClassObject() {
        assertEquals(3, wcCounterViaClass())    // 3
        assertEquals(20, wcCounterViaObject())  // 20
    }

    @TestAttribute
    fun funref_callableReferences() {
        val xs = listOf(1, 2, 3, 4, 5, 6)
        assertEquals("2,4,6", xs.filter(::funrefIsEven).joinToString(","))            // 2,4,6
        assertEquals("1,4,9,16,25,36", xs.map(::funrefSquare).joinToString(","))      // 1,4,9,16,25,36
        val f: (String) -> String = ::funrefGreet
        assertEquals("Hi, Kotlin", f("Kotlin"))                                       // Hi, Kotlin
        val c = FunrefCalc(100)
        val bound = c::addTo                                                          // bound instance ref
        assertEquals(105, bound(5))                                                   // 105
        assertEquals(107, funrefApply2(c::addTo, 7))                                  // 107
        val lbl: () -> String = c::label                                             // bound ref to an open method (ldvirtftn)
        assertEquals("calc100", lbl())                                               // calc100
        val unb = FunrefCalc::addTo                                                   // unbound (FunrefCalc, Int) -> Int
        assertEquals(203, unb(FunrefCalc(200), 3))                                    // 203
        assertEquals(42, funrefApplyTo(FunrefCalc::addTo, FunrefCalc(40), 2))         // 42
    }

    @TestAttribute
    fun extfunref_extensionReferences() {
        val lines = listOf("  hi ", "   ", "world", "")
        // unbound cross-module stdlib extension ref passed to a HOF (the Indent.kt case)
        assertEquals("  hi |world", lines.filter(String::isNotBlank).joinToString("|"))  // "  hi |world"
        val words = listOf("a", "hey", "hello")
        assertEquals("2,6,10", words.map(String::extDoubleLen).joinToString(","))        // 2,6,10
        val f: (String) -> String = String::extShout
        assertEquals("KOTLIN!", f("kotlin"))                                            // KOTLIN!
        val g: (String, Int) -> String = String::extRepeatBy
        assertEquals("ababab", g("ab", 3))                                              // ababab
        val sb = StringBuilder()
        val h: (String, StringBuilder) -> Unit = String::extLogTo
        h("a", sb); h("b", sb)
        assertEquals("[a][b]", sb.toString())                                           // [a][b]
    }

    @TestAttribute
    fun threadlambda_delegateOverload() {
        // #19: a bare no-arrow `{ }` into a .NET member overloaded on DELEGATE-typed params must resolve without
        // ambiguity. Thread({...}) overloads ThreadStart(()->Unit)/ParameterizedThreadStart((Any?)->Unit);
        // Task.Run({...}) overloads Action(()->Unit)/Func<T>(()->T). The bare lambda binds the PREFERRED sibling
        // (ThreadStart / Action). The lambda bodies are Unit-returning (trailing `Unit`, as `println` was) so the
        // Action-vs-Func<Unit> shape is unchanged. Join()/Wait() make the writes settle deterministically.
        val log = mutableListOf<String>()
        val t = Thread({ log.add("x"); Unit })   // bare lambda -> ThreadStart (was: ambiguity)
        t.Start(); t.Join()
        val task = Task.Run({ log.add("y"); Unit })  // bare lambda -> Action (was: ambiguity vs Func<T>)
        task.Wait()
        assertEquals("x|y", log.joinToString("|"))  // x / y  (done reached => both delegates invoked)
    }
}
