// #68 — a local `var` that is CAPTURED AND WRITTEN by a lambda / object expression / local class is promoted to a
// shared heap ref-cell, on EVERY emission root, not only inside a function body. LambdaTests.kt's
// `localClassObject` pins the function-body root (`il-writecapture`); this battery pins the rest of them, since each
// root emits user IR trees of its own:
//
//   constructor / init block            -> cvrcInit* (the issue's exact repro), cvrcSecondary
//   member property initializer         -> CvrcPropInit
//   member custom accessor              -> CvrcGetter
//   default interface method body       -> CvrcIface
//   top-level property initializer      -> cvrcTopLevel
//   companion static-field initializer  -> CvrcCompanion, CvrcIfaceCompanion
//   rich-enum entry argument            -> CvrcEnum
//   `@KotlinDefault` default-value expr -> cvrcDefaultArg
//
// The three shapes are one subject: the cell is keyed by the VARIABLE, so a lambda, an object expression and a local
// class writing the SAME `var` must all land in the SAME cell (cvrcInitMixed asserts that composition).
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

// One `var`, all three writers -> ONE shared cell.
class CvrcInitMixed {
    val total: Int
    init {
        var n = 0
        val f = { n++ }
        val o = object { fun go() { n += 10 } }
        class Bump { fun go() { n += 100 } }
        f(); o.go(); Bump().go()
        total = n
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
// The initializer is an immediately-invoked lambda so the captured `var` lives in the STATIC initializer itself. (Not
// `run { … }`: in initializer position that resolves to the extension `T.run`, whose implicit receiver is unavailable
// in a static initializer — an unrelated pre-existing gap.)
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
        assertEquals(111, CvrcInitMixed().total)     // 1 + 10 + 100
        assertEquals(10, CvrcInitNested().total)     // 5 + 5
        assertEquals(14, CvrcSecondary().total)      // 7 + 7
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
        assertEquals(9, CvrcEnum.COMPUTED.show() + CvrcEnum.PLAIN.show())  // 2 + 7
    }

    @TestAttribute
    fun defaultArgumentValue() {
        assertEquals(2, cvrcDefaultArg())            // the default expression's own cell
        assertEquals(5, cvrcDefaultArg(5))           // 5
    }
}
