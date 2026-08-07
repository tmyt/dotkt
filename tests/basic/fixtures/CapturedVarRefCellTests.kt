// #68 — a local `var` that is CAPTURED AND WRITTEN across a capture boundary is promoted to a shared heap ref-cell,
// under EVERY emission root and for EVERY boundary kind. Two axes:
//
// EMISSION ROOT (which emitter builds the tree). LambdaTests.kt's `localClassObject` pins the function-body root
// (`il-writecapture`); this battery pins the others:
//   the constructor emitter        -> CapturedVarRefCellInit* (the issue's exact repro), CapturedVarRefCellPropInit, CapturedVarRefCellSecondary
//                                     (init block, member property initializer, and secondary-constructor body)
//   member custom accessor         -> CapturedVarRefCellGetter
//   default interface method body  -> CapturedVarRefCellIface
//   top-level property initializer -> capturedVarRefCellTopLevel
//   static-field initializer       -> CapturedVarRefCellCompanion, CapturedVarRefCellIfaceCompanion (a companion's fields flatten onto the
//                                     enclosing class/interface statics), CapturedVarRefCellEnum (rich-enum entry argument)
//   default-value expression       -> capturedVarRefCellDefaultArg (the `@KotlinDefault` carrier + the omitting call site)
//
// BOUNDARY KIND (what captures the `var`): a lambda, a local `fun`, an object expression, and a local class. The cell
// is keyed by the VARIABLE, not by the boundary, so every boundary writing the SAME `var` lands in the SAME cell —
// CapturedVarRefCellInitMixed asserts that composition, and CapturedVarRefCellInitShared asserts the ENCLOSING frame's own write is visible
// through the cell too.
//
// The local-`fun` boundary differs from the other three: the lift turns its captures into BY-VALUE parameters of a
// static method, so a missing cell makes the write land on that parameter and the caller keep the stale value — no
// diagnostic at all, where the object/local-class boundaries abort. Reaching it from ANOTHER boundary failed loud
// instead, because the lift supplies those captures at the call site. `capturedVarRefCellLocalFun*`/`CapturedVarRefCellLocalFun*` pin both halves:
// the write itself (plain, in an `init` block, recursive, and typed by an enclosing TYPE PARAMETER so the cell is
// generic and must be constructed in the lifted method's own frame), and reaching the local fun through a lambda, an
// object expression or a local class, including when the receiving frame already has a value of that name — for a
// lifted CLASS that means one of its own fields, which is why a colliding capture field is renamed.
//
// Top-level names are family-prefixed (`capturedVarRefCell`/`CapturedVarRefCell`) — one project is one namespace, shared with the sibling batteries.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

// ---- constructor / init-block root -------------------------------------------------------------------------------
interface CapturedVarRefCellRunner { fun run() }

// The issue's exact repro: an object expression writing an init-block `var`.
class CapturedVarRefCellInitObject {
    val total: Int
    init {
        var n = 0
        val r = object : CapturedVarRefCellRunner { override fun run() { n++ } }
        r.run(); r.run()
        total = n
    }
}

// A local class writing an init-block `var`.
class CapturedVarRefCellInitLocalClass {
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
class CapturedVarRefCellInitLambda {
    val total: Int
    init {
        var n = 0
        val f = { n++ }
        f(); f()
        total = n
    }
}

// One `var`, every writer -> ONE shared cell.
class CapturedVarRefCellInitMixed {
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
class CapturedVarRefCellInitShared {
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
class CapturedVarRefCellInitNested {
    val total: Int
    init {
        var n = 0
        class Twice { fun go() { val step = { n += 5 }; step(); step() } }
        Twice().go()
        total = n
    }
}

// A SECONDARY constructor body (its statements are emitted by the same root, without the instance initializers).
class CapturedVarRefCellSecondary {
    val total: Int
    constructor() {
        var n = 0
        val o = object { fun go() { n += 7 } }
        o.go(); o.go()
        total = n
    }
}

// ---- member property-initializer root (not an init block) --------------------------------------------------------
class CapturedVarRefCellPropInit {
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
class CapturedVarRefCellGetter {
    val total: Int
        get() {
            var n = 0
            val o = object { fun go() { n++ } }
            o.go(); o.go()
            return n
        }
}

// ---- default interface method root -------------------------------------------------------------------------------
interface CapturedVarRefCellIface {
    fun total(): Int {
        var n = 0
        val o = object { fun go() { n += 2 } }
        o.go(); o.go()
        return n
    }
}
class CapturedVarRefCellIfaceImpl : CapturedVarRefCellIface

// ---- top-level property initializer root (a static field of the file class) --------------------------------------
val capturedVarRefCellTopLevel: Int = run {
    var n = 0
    val o = object { fun go() { n++ } }
    o.go(); o.go()
    n
}

// ---- static-field initializer roots (a companion's fields flatten onto the enclosing class/interface statics) -----
// The initializer is an immediately-invoked lambda, so the captured `var` lives in the STATIC initializer itself. It is
// deliberately not `run { … }`: in a COMPANION initializer that resolves to the extension `T.run` and the companion's
// implicit receiver is emitted into the static initializer, which fails independently of anything ref-cell related
// (a plain `val total: Int = run { var n = 0; n++; n }` companion property fails the same way). `capturedVarRefCellTopLevel` above
// can use `run` because a top-level initializer has no implicit receiver to bind.
class CapturedVarRefCellCompanion {
    companion object {
        val total: Int = {
            var n = 0
            val o = object { fun go() { n++ } }
            o.go(); o.go()
            n
        }()
    }
}
interface CapturedVarRefCellIfaceCompanion {
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
enum class CapturedVarRefCellEnum(val v: Int) {
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
fun capturedVarRefCellLocalFunInFunction(): Int {
    var n = 0
    fun bump() { n++ }
    bump(); bump()
    return n
}

class CapturedVarRefCellLocalFunInit {
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
class CapturedVarRefCellLocalFunGeneric<T>(val t: T, val other: T) {
    fun pick(): T {
        var cur = t
        fun set() { cur = other }
        set()
        return cur
    }
}

class CapturedVarRefCellLocalFunMixedFrame<T>(val first: T, val second: T) {
    fun pick(): T {
        var current = first
        fun <U> set(unused: U) { current = second }
        set(1)
        return current
    }
}

// The local lift uses only the SECOND parameter of its enclosing method frame. Its own dense method#0 therefore
// corresponds to the ref-cell registry's original method#1; preserving that authored edge is required when the cell
// is constructed in the lifted method's compacted generic frame.
fun <A, B> capturedVarRefCellSparseLocalFun(unused: A, value: B): B {
    var current = value
    fun set() { current = value }
    set()
    return current
}

// A lambda that only CALLS the local fun must capture what the local fun captures — the lift passes those values as
// leading arguments AT THE CALL SITE, which here sits inside the lambda's own frame.
fun capturedVarRefCellLocalFunViaClosure(): Int {
    var n = 0
    fun bump() { n++ }
    val g = { bump() }
    g(); g()
    return n
}

// A RECURSIVE local fun writing the captured `var`: the transitive-capture walk follows a call into a local fun, so
// it has to stop at the cycle instead of recursing forever.
fun capturedVarRefCellLocalFunRecursive(): Int {
    var n = 0
    fun down(k: Int) { if (k > 0) { n += k; down(k - 1) } }
    down(3)
    return n
}

// The local fun is declared in an `init` block, and only a LAMBDA calls it. A `fun` in `init { }` has the enclosing
// CLASS as its IR parent (an anonymous initializer cannot be a declaration parent), so recognizing it as local cannot
// be a test on the parent kind alone — it is absent from that class's declarations, unlike a real member.
class CapturedVarRefCellLocalFunInitViaClosure {
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
fun capturedVarRefCellLocalFunViaObject(): Int {
    var n = 0
    fun bump() { n++ }
    val o = object { fun go() { bump() } }
    o.go(); o.go()
    return n
}

fun capturedVarRefCellLocalFunViaLocalClass(): Int {
    var n = 0
    fun bump() { n++ }
    class L { fun go() { bump() } }
    L().go(); L().go()
    return n
}

// A transitive capture whose name is ALREADY TAKEN in the frame that receives it: `addTwice`'s own parameter is also
// called `n`, so the lifted static must not put two `n` parameters side by side and bind the wrong one.
fun capturedVarRefCellLocalFunShadowedCapture(): Int {
    var n = 0
    fun bump() { n++ }
    fun addTwice(n: Int) { if (n > 0) { bump(); bump() } }
    addTwice(1)
    return n
}

// The local fun is declared INSIDE a lambda. The lift must RESTORE the enclosing binding for `n` rather than drop it,
// or the `bump()` call that follows evaluates `n` as a bare local the closure's frame does not have.
fun capturedVarRefCellLocalFunInsideClosure(): Int {
    var n = 0
    val g = { fun bump() { n++ }; bump() }
    g(); g()
    return n
}

// A LOCAL CLASS constructed from inside a lambda: its captures ride the CONSTRUCTION site, so the lambda must capture
// them even though nothing in it mentions `n`.
fun capturedVarRefCellLocalClassCtorViaClosure(): Int {
    var n = 0
    class L { fun go() { n++ } }
    val g = { L().go() }
    g(); g()
    return n
}

// A local fun inside an `inner class` member reads the enclosing instance. Restoring (not dropping) the outer-`this`
// binding is what keeps a LATER member of the same class reading the enclosing instance instead of its own.
class CapturedVarRefCellOuter(val v: Int) {
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
// the two variables cannot satisfy interchangeably (`CapturedVarRefCellStrBox : CapturedVarRefCellBox<String>` is not a `CapturedVarRefCellBox<CapturedVarRefCellStrBox>`), so
// the assertion pins WHICH variable the lift's re-declared constraint resolves to, not merely that it resolves.
interface CapturedVarRefCellBox<X> { fun get(): X }
class CapturedVarRefCellStrBox(val s: String) : CapturedVarRefCellBox<String> { override fun get(): String = s }

fun <T, U> capturedVarRefCellLocalFunBoundedTv(a: T, b: T): T where T : CapturedVarRefCellBox<U> {
    var cur = a
    fun set() { cur = b }
    set()
    return cur
}

// A local class declared INSIDE a lambda: the lift must restore the lambda's own capture binding for `n`.
fun capturedVarRefCellLocalClassInsideClosure(): Int {
    var n = 0
    val g = { class L { fun go() { n++ } }; L().go() }
    g(); g()
    return n
}

// A local class INHERITING from a capturing local class. The base took its captures as leading ctor params when it was
// lifted, so the derived class must capture them too and forward them ahead of the source-level base arguments.
fun capturedVarRefCellLocalClassInheritance(): Int {
    var n = 0
    open class A { fun go() { n++ } }
    class B : A()
    B().go(); B().go()
    return n
}

// A reference to a non-capturing local fun: it targets the LIFTED static, not a file-class member under its own name.
fun capturedVarRefCellLocalFunReference(): Int {
    fun twice(k: Int): Int = k * 2
    val f: (Int) -> Int = ::twice
    return f(21)
}

// A reference to a CAPTURING local fun. The lift's captures ride ahead of the declared params, so the reference cannot
// be a plain delegate over the static — it is a closure holding the captured values whose `invoke` forwards to it.
fun capturedVarRefCellLocalFunReferenceCapturing(): Int {
    var n = 0
    fun bump(): Int { n++; return n }
    val f: () -> Int = ::bump
    f()
    return f()
}

// A callable reference is a declaration-reachability edge just like a direct call. Here that edge is itself nested
// in another capture boundary, so the outer lambda must transitively carry `bump`'s cell.
fun capturedVarRefCellLocalFunReferenceInsideClosure(): Int {
    var n = 0
    fun bump() { n++ }
    val run = { val f: () -> Unit = ::bump; f() }
    run(); run()
    return n
}

// A local CLASS constructor reference has the source constructor's arity, while the lifted ctor also takes hidden
// captures. The callable-reference adapter binds those captures without exposing them in `(Int) -> Bump`.
fun capturedVarRefCellLocalClassConstructorReference(): Int {
    var n = 0
    class Bump(val step: Int) { init { n += step } }
    val make: (Int) -> Bump = ::Bump
    make(2); make(3)
    return n
}

// A generic enclosing type parameter is re-declared by both the lifted local fun and its callable-reference closure.
fun <T> capturedVarRefCellGenericLocalFunReference(first: T, second: T): T {
    var current = first
    fun pick(): T { current = second; return current }
    val f: () -> T = ::pick
    return f()
}

// Distinct declarations with the same Kotlin spelling can be captured transitively by one lift. Frame slots and
// capture fields are allocated from declaration identity, so neither value aliases the other.
fun capturedVarRefCellDuplicateCaptureNames(): Int {
    var n = 0
    fun bumpOuter() { n++ }
    return run {
        var n = 10
        fun bumpBoth() { bumpOuter(); n++ }
        bumpBoth()
        n
    } + n
}

// A local variable shadowing a captured ref-cell must not replace that cell in ilemit's flat local table.
fun capturedVarRefCellShadowedFrameSlot(): Int {
    var n = 0
    fun bump() { n++ }
    if (n == 0) {
        val n = 100
        bump()
        if (n != 100) return -1
    }
    return n
}

// …including a capture of the enclosing INSTANCE, which is a capture like any other.
class CapturedVarRefCellRefCapturesThis {
    val k = 3
    fun run(): Int {
        fun add(x: Int) = x + k
        val f: (Int) -> Int = ::add
        return f(1)
    }
}

// `__outer` is a legal Kotlin parameter name, not a reserved capture slot. The captured enclosing instance is
// uniqued in the lifted method's parameter namespace and every use follows the emitted binding by identity.
class CapturedVarRefCellOuterNameCollision(val base: Int) {
    fun run(): Int {
        fun add(__outer: Int): Int = base + __outer
        return add(2)
    }
}

// A lifted class's SECONDARY constructor delegating with `this(...)`: every ctor of the class takes the same leading
// capture params, so the delegation forwards them ahead of the source-level arguments.
fun capturedVarRefCellLocalClassThisDelegate(): Int {
    var n = 0
    fun bump() { n++ }
    class L {
        val q: Int
        constructor() : this(1)
        constructor(v: Int) { q = v; bump() }
    }
    L(); L()
    return n
}

// A capture colliding with a BODY LOCAL of the lifted local fun. The lift's captures become parameters, and a
// `{k:local}` read resolves against body locals before parameters in one flat map, so a like-named local declared
// anywhere inside would shadow the capture from that point on — a wrong value, not a diagnostic.
fun capturedVarRefCellLocalFunBodyLocalShadow(): Int {
    val k = 1
    fun get(): Int = k
    fun outer(): Int { val k = 100; return get() + k }
    return outer()
}

// A capture colliding with a CONSTRUCTOR PARAMETER of the lifted class — a parameter with no backing field, so the
// collision is invisible if only the class's own fields are consulted.
fun capturedVarRefCellLocalClassCtorParamCollision(): Int {
    var n = 0
    fun bump() { n++ }
    class L(n: Int) { val m = n * 2; fun go(): Int { bump(); return m } }
    L(3).go(); L(3).go()
    return n
}

// Two generic classes in ONE file whose cell element types PRINT identically (both `T` at position 0) but whose bounds
// differ: they must not share a cell, or one gets the other's constraint.
class CapturedVarRefCellCellA<T : Comparable<T>>(val a: T, val b: T) {
    fun pick(): T { var cur = a; val g = { cur = b }; g(); return cur }
}
class CapturedVarRefCellCellB<T>(val a: T, val b: T) {
    fun pick(): T { var cur = a; val g = { cur = b }; g(); return cur }
}

// A local class that both DECLARES a field `n` and receives a capture named `n` (transitively, by calling `bump`).
// The two share one namespace in the lifted class, so the capture is renamed rather than shadowed — otherwise the
// write goes to the class's own field and the caller's `n` never moves.
fun capturedVarRefCellLocalClassFieldCollision(): Int {
    var n = 0
    fun bump() { n++ }
    class L { val n = 1; fun go() { bump() } }
    L().go(); L().go()
    return n
}

// The celled `var` is declared INSIDE the local fun and captured by a lambda there, so its generic cell has no use
// left in the frame that declares the type parameter — the lift itself has to supply the parameter's constraints.
class CapturedVarRefCellLocalFunInnerCell<T>(val a: T, val b: T) {
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
fun capturedVarRefCellDefaultArg(x: Int = run {
    var n = 0
    val o = object { fun go() { n++ } }
    o.go(); o.go()
    n
}): Int = x

class CapturedVarRefCellTests {
    @TestAttribute
    fun initBlock() {
        assertEquals(2, CapturedVarRefCellInitObject().total)      // 2
        assertEquals(6, CapturedVarRefCellInitLocalClass().total)  // 6
        assertEquals(2, CapturedVarRefCellInitLambda().total)      // 2
        assertEquals(1111, CapturedVarRefCellInitMixed().total)    // 1 + 1000 + 10 + 100
        assertEquals(30, CapturedVarRefCellInitShared().total)     // read() == 15 (the enclosing write is visible) + n == 15
        assertEquals(10, CapturedVarRefCellInitNested().total)     // 5 + 5
        assertEquals(14, CapturedVarRefCellSecondary().total)      // 7 + 7
    }

    @TestAttribute
    fun localFunBoundary() {
        assertEquals(2, capturedVarRefCellLocalFunInFunction())              // 2
        assertEquals(6, CapturedVarRefCellLocalFunInit().total)              // 3 + 3
        assertEquals(2, CapturedVarRefCellLocalFunGeneric(1, 2).pick())      // `other`, not `t`
        assertEquals("b", CapturedVarRefCellLocalFunGeneric("a", "b").pick())
        assertEquals("b", CapturedVarRefCellLocalFunMixedFrame("a", "b").pick())
        assertEquals("sparse", capturedVarRefCellSparseLocalFun(1, "sparse"))
        assertEquals(2, capturedVarRefCellLocalFunViaClosure())              // 2
        assertEquals(6, capturedVarRefCellLocalFunRecursive())               // 3 + 2 + 1
    }

    @TestAttribute
    fun localFunReachedThroughAnotherBoundary() {
        // The lift supplies a local fun's captures at the CALL SITE, so whichever boundary contains that call has to
        // capture them too — a lambda, an object expression or a local class alike, under any emission root.
        assertEquals(2, CapturedVarRefCellLocalFunInitViaClosure().total)     // 2
        assertEquals(2, capturedVarRefCellLocalFunViaObject())                // 2
        assertEquals(2, capturedVarRefCellLocalFunViaLocalClass())            // 2
        assertEquals(2, capturedVarRefCellLocalFunShadowedCapture())          // 2; a lost write would give 0
        assertEquals(2, CapturedVarRefCellLocalFunInnerCell(1, 2).pick())     // `b`
        assertEquals("b", CapturedVarRefCellLocalFunInnerCell("a", "b").pick())
    }

    @TestAttribute
    fun localDeclarationReachedFromAnotherLift() {
        // Reaching a local fun / local class from INSIDE another lift: the enclosing frame's capture binding has to
        // survive the inner lift, and a construction site propagates captures just like a call site.
        assertEquals(2, capturedVarRefCellLocalFunInsideClosure())             // 2
        assertEquals(2, capturedVarRefCellLocalClassCtorViaClosure())          // 2
        val outer = CapturedVarRefCellOuter(7)
        assertEquals(7, outer.Inner().viaLocalFun())             // 7
        assertEquals(7, outer.Inner().direct())                  // 7 — the outer-`this` binding survived
        assertEquals(2, capturedVarRefCellLocalClassInsideClosure())           // 2
        assertEquals(2, capturedVarRefCellLocalClassFieldCollision())          // 2, not the class's own `n`
        assertEquals(2, capturedVarRefCellLocalClassInheritance())             // 2 — the base's captures reach it through `B : A()`
        assertEquals(42, capturedVarRefCellLocalFunReference())                // 42 — `::twice` targets the lifted static
        assertEquals(2, capturedVarRefCellLocalFunReferenceCapturing())        // 2 — the reference carries the captured cell
        assertEquals(2, capturedVarRefCellLocalFunReferenceInsideClosure())    // reference reachability propagates through lambda
        assertEquals(5, capturedVarRefCellLocalClassConstructorReference())    // hidden ctor captures stay bound in the closure
        assertEquals(2, capturedVarRefCellGenericLocalFunReference(1, 2))      // generic capturing local-fun reference
        assertEquals("b", capturedVarRefCellGenericLocalFunReference("a", "b"))
        assertEquals(12, capturedVarRefCellDuplicateCaptureNames())            // inner 11 + outer 1
        assertEquals(1, capturedVarRefCellShadowedFrameSlot())                 // shadow did not replace the outer ref-cell
        assertEquals(9, CapturedVarRefCellOuterNameCollision(7).run())         // user `__outer` and captured this stay distinct
        assertEquals(4, CapturedVarRefCellRefCapturesThis().run())             // 1 + 3, the enclosing instance captured
        assertEquals(2, capturedVarRefCellLocalClassThisDelegate())            // 2 — `this(1)` forwarded the capture
        assertEquals(2, capturedVarRefCellLocalClassCtorParamCollision())      // 2, not the ctor parameter's `n`
        assertEquals(101, capturedVarRefCellLocalFunBodyLocalShadow())         // 1 + 100, not the body local read twice
        assertEquals(2, CapturedVarRefCellCellA(1, 2).pick())                  // distinct cells despite identical printed elements
        assertEquals(4, CapturedVarRefCellCellB(3, 4).pick())
        assertEquals("y", capturedVarRefCellLocalFunBoundedTv<CapturedVarRefCellStrBox, String>(CapturedVarRefCellStrBox("x"), CapturedVarRefCellStrBox("y")).get())
    }

    @TestAttribute
    fun propertyInitializer() {
        assertEquals(2, CapturedVarRefCellPropInit().total)                    // 2
        assertEquals(84, CapturedVarRefCellPropInit.WithParam(42).total)       // read-only ctor-param capture: 42 * 2
    }

    @TestAttribute
    fun accessorAndInterfaceDefault() {
        assertEquals(2, CapturedVarRefCellGetter().total)          // 2
        assertEquals(4, CapturedVarRefCellIfaceImpl().total())     // 2 + 2
    }

    @TestAttribute
    fun staticInitializers() {
        assertEquals(2, capturedVarRefCellTopLevel)                // 2
        assertEquals(2, CapturedVarRefCellCompanion.total)         // 2
        assertEquals(8, CapturedVarRefCellIfaceCompanion.total)    // 4 + 4
        assertEquals(2, CapturedVarRefCellEnum.COMPUTED.show())    // 2
        assertEquals(7, CapturedVarRefCellEnum.PLAIN.show())       // 7
    }

    @TestAttribute
    fun defaultArgumentValue() {
        assertEquals(2, capturedVarRefCellDefaultArg())            // the default expression's own cell
        assertEquals(5, capturedVarRefCellDefaultArg(5))           // 5
    }
}
