// #68 — a local `var` that is CAPTURED AND WRITTEN across a capture boundary is promoted to a shared heap ref-cell,
// under EVERY emission root and for EVERY boundary kind. Two axes:
//
// EMISSION ROOT (which emitter builds the tree). LambdaTests.kt's `localClassObject` pins the function-body root
// (`il-writecapture`); this battery pins the others:
//   the constructor emitter        -> CvrcInit* (the issue's exact repro), CvrcPropInit, CvrcSecondary
//                                     (init block, member property initializer, and secondary-constructor body)
//   member custom accessor         -> CvrcGetter
//   default interface method body  -> CvrcIface
//   top-level property initializer -> cvrcTopLevel
//   static-field initializer       -> CvrcCompanion, CvrcIfaceCompanion (a companion's fields flatten onto the
//                                     enclosing class/interface statics), CvrcEnum (rich-enum entry argument)
//   default-value expression       -> cvrcDefaultArg (the `@KotlinDefault` carrier + the omitting call site)
//
// BOUNDARY KIND (what captures the `var`): a lambda, a local `fun`, an object expression, and a local class. The cell
// is keyed by the VARIABLE, not by the boundary, so every boundary writing the SAME `var` lands in the SAME cell —
// CvrcInitMixed asserts that composition, and CvrcInitShared asserts the ENCLOSING frame's own write is visible
// through the cell too.
//
// The local-`fun` boundary differs from the other three: the lift turns its captures into BY-VALUE parameters of a
// static method, so a missing cell makes the write land on that parameter and the caller keep the stale value — no
// diagnostic at all, where the object/local-class boundaries abort. Reaching it from ANOTHER boundary failed loud
// instead, because the lift supplies those captures at the call site. `cvrcLocalFun*`/`CvrcLocalFun*` pin both halves:
// the write itself (plain, in an `init` block, recursive, and typed by an enclosing TYPE PARAMETER so the cell is
// generic and must be constructed in the lifted method's own frame), and reaching the local fun through a lambda, an
// object expression or a local class, including when the receiving frame already has a value of that name — for a
// lifted CLASS that means one of its own fields, which is why a colliding capture field is renamed.
//
// Top-level names are family-prefixed (`cvrc`/`Cvrc`) — one project is one namespace, shared with the sibling batteries.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

// ---- constructor / init-block root -------------------------------------------------------------------------------
interface CvrcRunner { fun run() }

// The issue's exact repro: an object expression writing an init-block `var`.
class CvrcInitObject {
    val total: Int
    init {
        var n = 0
        val r = object : CvrcRunner { override fun run() { n++ } }
        r.run(); r.run()
        total = n
    }
}

// A local class writing an init-block `var`.
class CvrcInitLocalClass {
    val total: Int
    init {
        var n = 0
        class Bump { fun go() { n += 3 } }
        val b = Bump()
        b.go(); b.go()
        total = n
    }
}

// A plain LAMBDA writing an init-block `var`. Unlike the object/local-class shapes this one is unguarded: without a
// cell the write emits as a `setLocal` inside the lifted closure's `invoke`, where no such local exists.
class CvrcInitLambda {
    val total: Int
    init {
        var n = 0
        val f = { n++ }
        f(); f()
        total = n
    }
}

// One `var`, every writer -> ONE shared cell.
class CvrcInitMixed {
    val total: Int
    init {
        var n = 0
        val f = { n++ }
        fun g() { n += 1000 }
        val o = object { fun go() { n += 10 } }
        class Bump { fun go() { n += 100 } }
        f(); g(); o.go(); Bump().go()
        total = n
    }
}

// The cell is shared in BOTH directions: a write made by the enclosing frame itself, after the closures exist, is
// visible to a closure that reads the `var`.
class CvrcInitShared {
    val total: Int
    init {
        var n = 1
        val read = { n }
        val scale = { n *= 10 }
        scale()      // 10, written through the cell
        n += 5       // 15, written by the ENCLOSING frame
        total = read() + n
    }
}

// A local class declared in an init block whose MEMBER holds a lambda writing the same captured `var` (the capture
// crosses two boundaries and must resolve to the one cell).
class CvrcInitNested {
    val total: Int
    init {
        var n = 0
        class Twice { fun go() { val step = { n += 5 }; step(); step() } }
        Twice().go()
        total = n
    }
}

// A SECONDARY constructor body (its statements are emitted by the same root, without the instance initializers).
class CvrcSecondary {
    val total: Int
    constructor() {
        var n = 0
        val o = object { fun go() { n += 7 } }
        o.go(); o.go()
        total = n
    }
}

// ---- member property-initializer root (not an init block) --------------------------------------------------------
class CvrcPropInit {
    val total: Int = run {
        var n = 0
        val o = object { fun go() { n++ } }
        o.go(); o.go()
        n
    }
    // No-regression pin: a read-only capture of a CONSTRUCTOR PARAMETER stays a plain value capture (a Kotlin
    // parameter is immutable, so it never becomes a cell).
    class WithParam(seed: Int) {
        val total: Int = run {
            val o = object { fun get(): Int = seed * 2 }
            o.get()
        }
    }
}

// ---- member custom accessor root ---------------------------------------------------------------------------------
class CvrcGetter {
    val total: Int
        get() {
            var n = 0
            val o = object { fun go() { n++ } }
            o.go(); o.go()
            return n
        }
}

// ---- default interface method root -------------------------------------------------------------------------------
interface CvrcIface {
    fun total(): Int {
        var n = 0
        val o = object { fun go() { n += 2 } }
        o.go(); o.go()
        return n
    }
}
class CvrcIfaceImpl : CvrcIface

// ---- top-level property initializer root (a static field of the file class) --------------------------------------
val cvrcTopLevel: Int = run {
    var n = 0
    val o = object { fun go() { n++ } }
    o.go(); o.go()
    n
}

// ---- static-field initializer roots (a companion's fields flatten onto the enclosing class/interface statics) -----
// The initializer is an immediately-invoked lambda, so the captured `var` lives in the STATIC initializer itself. It is
// deliberately not `run { … }`: in a COMPANION initializer that resolves to the extension `T.run` and the companion's
// implicit receiver is emitted into the static initializer, which fails independently of anything ref-cell related
// (a plain `val total: Int = run { var n = 0; n++; n }` companion property fails the same way). `cvrcTopLevel` above
// can use `run` because a top-level initializer has no implicit receiver to bind.
class CvrcCompanion {
    companion object {
        val total: Int = {
            var n = 0
            val o = object { fun go() { n++ } }
            o.go(); o.go()
            n
        }()
    }
}
interface CvrcIfaceCompanion {
    companion object {
        val total: Int = {
            var n = 0
            val o = object { fun go() { n += 4 } }
            o.go(); o.go()
            n
        }()
    }
}

// ---- rich-enum entry-argument root -------------------------------------------------------------------------------
enum class CvrcEnum(val v: Int) {
    COMPUTED({
        var n = 0
        val o = object { fun go() { n++ } }
        o.go(); o.go()
        n
    }()),
    PLAIN(7);
    fun show(): Int = v
}

// ---- the local-`fun` boundary --------------------------------------------------------------------------------------
// A local fun lifts to a static method whose captures are BY-VALUE parameters, so without a cell a write through it
// lands on the lifted method's own parameter and the caller keeps the stale value.
fun cvrcLocalFunInFunction(): Int {
    var n = 0
    fun bump() { n++ }
    bump(); bump()
    return n
}

class CvrcLocalFunInit {
    val total: Int
    init {
        var n = 0
        fun bump() { n += 3 }
        bump(); bump()
        total = n
    }
}

// The captured `var` is typed by an enclosing TYPE PARAMETER, so its cell is generic. The lifted static re-declares
// the enclosing type params as its OWN method params while the cell's element type is registered in the enclosing
// frame, so the cell use inside the lift has to be constructed in the method's parameter space.
class CvrcLocalFunGeneric<T>(val t: T, val other: T) {
    fun pick(): T {
        var cur = t
        fun set() { cur = other }
        set()
        return cur
    }
}

// A lambda that only CALLS the local fun must capture what the local fun captures — the lift passes those values as
// leading arguments AT THE CALL SITE, which here sits inside the lambda's own frame.
fun cvrcLocalFunViaClosure(): Int {
    var n = 0
    fun bump() { n++ }
    val g = { bump() }
    g(); g()
    return n
}

// A RECURSIVE local fun writing the captured `var`: the transitive-capture walk follows a call into a local fun, so
// it has to stop at the cycle instead of recursing forever.
fun cvrcLocalFunRecursive(): Int {
    var n = 0
    fun down(k: Int) { if (k > 0) { n += k; down(k - 1) } }
    down(3)
    return n
}

// The local fun is declared in an `init` block, and only a LAMBDA calls it. A `fun` in `init { }` has the enclosing
// CLASS as its IR parent (an anonymous initializer cannot be a declaration parent), so recognizing it as local cannot
// be a test on the parent kind alone — it is absent from that class's declarations, unlike a real member.
class CvrcLocalFunInitViaClosure {
    val total: Int
    init {
        var n = 0
        fun bump() { n++ }
        val g = { bump() }
        g(); g()
        total = n
    }
}

// An OBJECT EXPRESSION and a LOCAL CLASS whose member only calls the local fun: the same transitive capture, through
// the other two boundary kinds.
fun cvrcLocalFunViaObject(): Int {
    var n = 0
    fun bump() { n++ }
    val o = object { fun go() { bump() } }
    o.go(); o.go()
    return n
}

fun cvrcLocalFunViaLocalClass(): Int {
    var n = 0
    fun bump() { n++ }
    class L { fun go() { bump() } }
    L().go(); L().go()
    return n
}

// A transitive capture whose name is ALREADY TAKEN in the frame that receives it: `addTwice`'s own parameter is also
// called `n`, so the lifted static must not put two `n` parameters side by side and bind the wrong one.
fun cvrcLocalFunShadowedCapture(): Int {
    var n = 0
    fun bump() { n++ }
    fun addTwice(n: Int) { if (n > 0) { bump(); bump() } }
    addTwice(1)
    return n
}

// The local fun is declared INSIDE a lambda. The lift must RESTORE the enclosing binding for `n` rather than drop it,
// or the `bump()` call that follows evaluates `n` as a bare local the closure's frame does not have.
fun cvrcLocalFunInsideClosure(): Int {
    var n = 0
    val g = { fun bump() { n++ }; bump() }
    g(); g()
    return n
}

// A LOCAL CLASS constructed from inside a lambda: its captures ride the CONSTRUCTION site, so the lambda must capture
// them even though nothing in it mentions `n`.
fun cvrcLocalClassCtorViaClosure(): Int {
    var n = 0
    class L { fun go() { n++ } }
    val g = { L().go() }
    g(); g()
    return n
}

// A local fun inside an `inner class` member reads the enclosing instance. Restoring (not dropping) the outer-`this`
// binding is what keeps a LATER member of the same class reading the enclosing instance instead of its own.
class CvrcOuter(val v: Int) {
    inner class Inner {
        fun viaLocalFun(): Int {
            fun g(): Int = v
            return g()
        }
        fun direct(): Int = v
    }
}

// A captured `var` whose type parameter's BOUND names a SECOND parameter: the lift re-declares the bound with the
// parameter, so it has to be generic over both or the re-declared constraint is unbound. The bound is deliberately one
// the two variables cannot satisfy interchangeably (`CvrcStrBox : CvrcBox<String>` is not a `CvrcBox<CvrcStrBox>`), so
// the assertion pins WHICH variable the lift's re-declared constraint resolves to, not merely that it resolves.
interface CvrcBox<X> { fun get(): X }
class CvrcStrBox(val s: String) : CvrcBox<String> { override fun get(): String = s }

fun <T, U> cvrcLocalFunBoundedTv(a: T, b: T): T where T : CvrcBox<U> {
    var cur = a
    fun set() { cur = b }
    set()
    return cur
}

// A local class declared INSIDE a lambda: the lift must restore the lambda's own capture binding for `n`.
fun cvrcLocalClassInsideClosure(): Int {
    var n = 0
    val g = { class L { fun go() { n++ } }; L().go() }
    g(); g()
    return n
}

// A local class that both DECLARES a field `n` and receives a capture named `n` (transitively, by calling `bump`).
// The two share one namespace in the lifted class, so the capture is renamed rather than shadowed — otherwise the
// write goes to the class's own field and the caller's `n` never moves.
fun cvrcLocalClassFieldCollision(): Int {
    var n = 0
    fun bump() { n++ }
    class L { val n = 1; fun go() { bump() } }
    L().go(); L().go()
    return n
}

// The celled `var` is declared INSIDE the local fun and captured by a lambda there, so its generic cell has no use
// left in the frame that declares the type parameter — the lift itself has to supply the parameter's constraints.
class CvrcLocalFunInnerCell<T>(val a: T, val b: T) {
    fun pick(): T {
        fun inner(): T {
            var cur = a
            val g = { cur = b }
            g()
            return cur
        }
        return inner()
    }
}

// ---- default-argument value root (the `@KotlinDefault` carrier + the omitting call site) -------------------------
fun cvrcDefaultArg(x: Int = run {
    var n = 0
    val o = object { fun go() { n++ } }
    o.go(); o.go()
    n
}): Int = x

class CapturedVarRefCellTests {
    @TestAttribute
    fun initBlock() {
        assertEquals(2, CvrcInitObject().total)      // 2
        assertEquals(6, CvrcInitLocalClass().total)  // 6
        assertEquals(2, CvrcInitLambda().total)      // 2
        assertEquals(1111, CvrcInitMixed().total)    // 1 + 1000 + 10 + 100
        assertEquals(30, CvrcInitShared().total)     // read() == 15 (the enclosing write is visible) + n == 15
        assertEquals(10, CvrcInitNested().total)     // 5 + 5
        assertEquals(14, CvrcSecondary().total)      // 7 + 7
    }

    @TestAttribute
    fun localFunBoundary() {
        assertEquals(2, cvrcLocalFunInFunction())              // 2
        assertEquals(6, CvrcLocalFunInit().total)              // 3 + 3
        assertEquals(2, CvrcLocalFunGeneric(1, 2).pick())      // `other`, not `t`
        assertEquals("b", CvrcLocalFunGeneric("a", "b").pick())
        assertEquals(2, cvrcLocalFunViaClosure())              // 2
        assertEquals(6, cvrcLocalFunRecursive())               // 3 + 2 + 1
    }

    @TestAttribute
    fun localFunReachedThroughAnotherBoundary() {
        // The lift supplies a local fun's captures at the CALL SITE, so whichever boundary contains that call has to
        // capture them too — a lambda, an object expression or a local class alike, under any emission root.
        assertEquals(2, CvrcLocalFunInitViaClosure().total)     // 2
        assertEquals(2, cvrcLocalFunViaObject())                // 2
        assertEquals(2, cvrcLocalFunViaLocalClass())            // 2
        assertEquals(2, cvrcLocalFunShadowedCapture())          // 2; a lost write would give 0
        assertEquals(2, CvrcLocalFunInnerCell(1, 2).pick())     // `b`
        assertEquals("b", CvrcLocalFunInnerCell("a", "b").pick())
    }

    @TestAttribute
    fun localDeclarationReachedFromAnotherLift() {
        // Reaching a local fun / local class from INSIDE another lift: the enclosing frame's capture binding has to
        // survive the inner lift, and a construction site propagates captures just like a call site.
        assertEquals(2, cvrcLocalFunInsideClosure())             // 2
        assertEquals(2, cvrcLocalClassCtorViaClosure())          // 2
        val outer = CvrcOuter(7)
        assertEquals(7, outer.Inner().viaLocalFun())             // 7
        assertEquals(7, outer.Inner().direct())                  // 7 — the outer-`this` binding survived
        assertEquals(2, cvrcLocalClassInsideClosure())           // 2
        assertEquals(2, cvrcLocalClassFieldCollision())          // 2, not the class's own `n`
        assertEquals("y", cvrcLocalFunBoundedTv<CvrcStrBox, String>(CvrcStrBox("x"), CvrcStrBox("y")).get())
    }

    @TestAttribute
    fun propertyInitializer() {
        assertEquals(2, CvrcPropInit().total)                    // 2
        assertEquals(84, CvrcPropInit.WithParam(42).total)       // read-only ctor-param capture: 42 * 2
    }

    @TestAttribute
    fun accessorAndInterfaceDefault() {
        assertEquals(2, CvrcGetter().total)          // 2
        assertEquals(4, CvrcIfaceImpl().total())     // 2 + 2
    }

    @TestAttribute
    fun staticInitializers() {
        assertEquals(2, cvrcTopLevel)                // 2
        assertEquals(2, CvrcCompanion.total)         // 2
        assertEquals(8, CvrcIfaceCompanion.total)    // 4 + 4
        assertEquals(2, CvrcEnum.COMPUTED.show())    // 2
        assertEquals(7, CvrcEnum.PLAIN.show())       // 7
    }

    @TestAttribute
    fun defaultArgumentValue() {
        assertEquals(2, cvrcDefaultArg())            // the default expression's own cell
        assertEquals(5, cvrcDefaultArg(5))           // 5
    }
}
