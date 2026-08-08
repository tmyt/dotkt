// Kotlin 2.4 `CompanionBlocksAndExtensions` (#382): `class C { companion { … } }` and top-level
// `companion fun C.f()` / `companion val C.p` as NATIVE Kotlin/CLR static declarations — no `@ClrStatic`.
//
// This battery is the SAME-MODULE half: the declarations compile to static members and behave as statics at run
// time. The cross-module half — DLL -> KLIB -> a second module resolving `C.f(...)` from metadata alone — lives in
// tests/roundtrip (producer/CompanionStatics.kt + consumer/CompanionStaticTests.kt), because that is the only place
// a consumer reads the built assembly instead of the source.
//
// Covered here:
//   - a companion-block fun, including OVERLOADS and a private member reached from a sibling companion member
//   - companion-block `val`/`var`/`const val`: static storage initialized in the TYPE initializer, not a ctor
//   - a companion block on a NESTED class, an INNER class, an INTERFACE and an ENUM class
//   - a generic owner: the statics are ONE logical member, not one per closed generic type
//   - callable references to both a companion-block fun and a companion-block property
//   - a real `companion object` DECLARED ALONGSIDE a companion block stays a distinct singleton
//   - companion EXTENSIONS: fun, computed `val`, backed `val`, `var`
import kotlin.clr.ClrField
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse

// ---- companion block: functions, overloads, visibility, storage ------------------------------------------------

class CompanionCounter(val n: Int) {
    companion {
        fun twice(x: Int): Int = x * 2
        fun twice(s: String): String = s + s
        val origin: CompanionCounter = CompanionCounter(0)
        var seen: Int = 1
        private val secret: Int = 9
        // Reads a private sibling AND another companion-block member with no qualifier — both are statics of
        // this same type, so neither needs a receiver.
        fun peek(): Int = secret + twice(1)
        const val TAG: String = "counter"
    }

    // A real companion object declared alongside the block: a separate singleton with INSTANCE members.
    companion object {
        val label: String = "real-companion"
        fun describe(): String = "obj:" + label
    }

    fun bump(): Int = n + 1
}

// ---- companion block on a generic owner ------------------------------------------------------------------------

class CompanionBox<T>(val v: T) {
    companion {
        fun make(): String = "box"
        var count: Int = 0
    }
}

// ---- companion block on nested / inner classes -----------------------------------------------------------------

class CompanionOuter {
    class Nested {
        companion { fun hi(): String = "nested" }
    }
    inner class Inner {
        companion { fun yo(): String = "inner" }
    }
}

// ---- companion block on an interface and on enum classes -------------------------------------------------------

interface CompanionShape {
    fun area(): Int
    companion {
        fun unit(): CompanionShape = object : CompanionShape { override fun area(): Int = 1 }
        // An interface may not carry a property INITIALIZER, so this one is computed.
        val kind: String get() = "shape"
    }
}

enum class CompanionColor {
    RED, GREEN;
    companion {
        fun best(): CompanionColor = GREEN
        val fallback: String = "red"
    }
}

// An enum whose companion block declares ONLY a property: it still needs the plain-class shape, because an
// ECMA-335 enum TypeDef may carry no non-literal static field.
enum class CompanionSimple {
    A, B;
    companion { val first: String = "a" }
}

// ---- companion-block properties whose storage IS the user-visible member --------------------------------------

class CompanionFieldRouted {
    companion {
        // Neither emits an accessor — the storage is the member — so the access site must address the storage, not
        // a `get_`/`set_` slot that does not exist.
        lateinit var late: String
        @ClrField var plain: Int = 1
    }
}

// A user property whose name collides with the compiler-reserved singleton field of its own `object`. The property's
// storage is renamed to keep the CLR type's members distinguishable; the singleton must keep its name.
object CompanionInstanceNameClash { val INSTANCE = 7 }

// ---- companion extensions ---------------------------------------------------------------------------------------

class CompanionTag(val label: String)

companion fun CompanionTag.of(label: String): CompanionTag = CompanionTag(label)
companion val CompanionTag.blank: CompanionTag get() = CompanionTag("")
companion val CompanionTag.marker: String = "m"
companion var CompanionTag.counter: Int = 0

class CompanionStaticTests {
    @TestAttribute
    fun companionBlockFunctionsAreStaticMembersOfTheirClass() {
        assertEquals(42, CompanionCounter.twice(21))
        assertEquals("abab", CompanionCounter.twice("ab"))
        assertEquals(11, CompanionCounter.peek())
        // Instance members of the same class are untouched by the block.
        assertEquals(5, CompanionCounter(4).bump())
    }

    @TestAttribute
    fun companionBlockStorageIsStaticAndTypeInitialized() {
        // `origin` was built by the type initializer, not by any constructor: constructing more instances cannot
        // change it, and it is the same reference every time it is read.
        val first = CompanionCounter.origin
        CompanionCounter(7)
        assertEquals(0, CompanionCounter.origin.n)
        assertTrue(first === CompanionCounter.origin)
        assertEquals("counter", CompanionCounter.TAG)

        CompanionCounter.seen = 7
        assertEquals(7, CompanionCounter.seen)
        CompanionCounter.seen = 1
    }

    @TestAttribute
    fun companionBlockOnAGenericOwnerIsOneLogicalMember() {
        assertEquals("box", CompanionBox.make())
        // Kotlin declares ONE `count`, so writing it through one closed instantiation must be visible through
        // every other one — the CLR's per-closed-generic static storage is deliberately collapsed.
        CompanionBox.count = 3
        assertEquals(3, CompanionBox.count)
        assertEquals("x", CompanionBox("x").v)
        assertEquals(1, CompanionBox(1).v)
        assertEquals(3, CompanionBox.count)
        CompanionBox.count = 0
    }

    @TestAttribute
    fun companionBlockOnNestedAndInnerClasses() {
        assertEquals("nested", CompanionOuter.Nested.hi())
        assertEquals("inner", CompanionOuter.Inner.yo())
    }

    @TestAttribute
    fun companionBlockOnAnInterfaceAndOnEnums() {
        assertEquals(1, CompanionShape.unit().area())
        assertEquals("shape", CompanionShape.kind)
        assertEquals(CompanionColor.GREEN, CompanionColor.best())
        assertEquals("red", CompanionColor.fallback)
        assertEquals("a", CompanionSimple.first)
        // The enum itself still behaves like an enum.
        assertEquals(2, CompanionColor.values().size)
        assertEquals(CompanionColor.RED, CompanionColor.valueOf("RED"))
    }

    @TestAttribute
    fun companionBlockMembersAreReferenceable() {
        val f: (Int) -> Int = CompanionCounter::twice
        assertEquals(10, f(5))
        val g: (String) -> String = CompanionCounter::twice
        assertEquals("zz", g("z"))
        val p = CompanionCounter::seen
        assertEquals(1, p.get())
        p.set(4)
        assertEquals(4, CompanionCounter.seen)
        p.set(1)
        val h: () -> String = CompanionOuter.Nested::hi
        assertEquals("nested", h())
    }

    @TestAttribute
    fun realCompanionObjectStaysDistinctFromACompanionBlock() {
        assertEquals("obj:real-companion", CompanionCounter.describe())
        assertEquals("real-companion", CompanionCounter.label)
        // A companion object is a singleton VALUE; the block's members have no instance at all.
        assertTrue(CompanionCounter.Companion === CompanionCounter.Companion)
        assertFalse(CompanionCounter.Companion.label == CompanionCounter.TAG)
    }

    @TestAttribute
    fun fieldRoutedCompanionBlockPropertiesAddressTheirStorage() {
        CompanionFieldRouted.late = "x"
        assertEquals("x", CompanionFieldRouted.late)
        CompanionFieldRouted.plain = 3
        assertEquals(3, CompanionFieldRouted.plain)
        CompanionFieldRouted.plain = 1
    }

    @TestAttribute
    fun anObjectSingletonKeepsItsNameBesideASameNamedProperty() {
        assertEquals(7, CompanionInstanceNameClash.INSTANCE)
        assertTrue(CompanionInstanceNameClash === CompanionInstanceNameClash)
    }

    @TestAttribute
    fun companionExtensionsResolveAndRun() {
        assertEquals("hi", CompanionTag.of("hi").label)
        assertEquals("", CompanionTag.blank.label)
        assertEquals("m", CompanionTag.marker)
        CompanionTag.counter = 4
        assertEquals(4, CompanionTag.counter)
        CompanionTag.counter = 0
    }
}
