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
// BOUNDARY KIND (what captures the `var`): a lambda, a local `fun` (lifted to a static method whose captures are
// by-value params — without the cell it writes its own parameter and the update is lost), an object expression, and a
// local class. The cell is keyed by the VARIABLE, not by the boundary, so all four writing the SAME `var` must land in
// the SAME cell — CvrcInitMixed asserts that composition, and CvrcInitShared asserts the ENCLOSING frame's own write
// is visible through the cell too.
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

// A LOCAL FUN writing an init-block `var`. It lifts to a static method that takes its captures as by-value
// parameters, so without the cell the increment lands on the lifted method's own parameter and is lost.
class CvrcInitLocalFun {
    val total: Int
    init {
        var n = 0
        fun bump() { n += 2 }
        bump(); bump()
        total = n
    }
}

// One `var`, all four writers -> ONE shared cell.
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
// The initializer is an immediately-invoked lambda, so the captured `var` lives in the STATIC initializer itself.
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

// ---- the local-`fun` boundary in a plain function body -----------------------------------------------------------
fun cvrcLocalFunInFunction(): Int {
    var n = 0
    fun bump() { n++ }
    bump(); bump()
    return n
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
        assertEquals(4, CvrcInitLocalFun().total)    // 2 + 2
        assertEquals(1111, CvrcInitMixed().total)    // 1 + 1000 + 10 + 100
        assertEquals(30, CvrcInitShared().total)     // read() == 15 (the enclosing write is visible) + n == 15
        assertEquals(10, CvrcInitNested().total)     // 5 + 5
        assertEquals(14, CvrcSecondary().total)      // 7 + 7
    }

    @TestAttribute
    fun functionBodyLocalFun() {
        // The local-`fun` boundary in the function-body root — the sibling of LambdaTests.localClassObject, which
        // covers the object-expression and local-class boundaries there.
        assertEquals(2, cvrcLocalFunInFunction())    // 2
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
