// CLR-relation language battery (feature fixture) — pure-Kotlin cases/il-* whose SUBJECT is how a Kotlin construct maps
// onto a CLR slot (SAM conversion, the System.Object super-slot, a @ClrTypeAlias-base property override, tuple/data
// -class toString). Migrated onto the in-process NUnit suite: each old case's `main` + stdout-golden becomes one
// @TestAttribute method whose per-value assert is strictly stronger (typed) than the old string diff; every asserted
// value is preserved 1:1 (see the `// <expected>` comments). No `import System.*` — these are plain-Kotlin fixtures.
//
// Coverage preserved (old case -> method):
//   il-samcmp      -> samComparatorLiteral    an explicit `Comparator { a, b -> ... }` SAM conversion -> a synthetic class implementing the plain Kotlin fun interface (no @Clr* read in kotc)
//   il-superobj    -> superToObjectSlot       #14 RESIDUAL: super.toString()/hashCode()/equals() to kotlin.Any reach the System.Object slot NON-virtually (else callvirt re-dispatches to the override -> stack overflow)
//   il-overridemsg -> overrideExceptionMessage #24 `override val message` on a @ClrTypeAlias base (kotlin.Exception->System.Exception) is DISPATCHED — the dedicated accessor keeps its name and an explicit MethodImpl fills the @ClrProperty("Message") slot
//   il-pairtostr   -> setTripleDataClassToString  collection/tuple/data-class toString routing (C11 gate guard)
//
// PARTIAL DUP — il-pairtostr's `listOf(1,2,3).toString()` (MapsTests), `(1 to 2).toString()` (ClrObjectModelLangTests),
// `"Aa".hashCode()==` (StringsTests) are already covered; only the unique `setOf(1,2,3).toString()`,
// `Triple(1,2,3).toString()` and data-class `Rec(name=k, n=9)` format are migrated here.
//
// Top-level names are family-prefixed with `ClrObjectModel` (one assembly = one namespace). The data class was `Rec`;
// renamed `ClrObjectModelRec` for collision-freedom, so its auto-toString reads `ClrObjectModelRec(...)` — the class name is part of
// the data-class toString value (the old golden's `Rec(name=k, n=9)` differs only by that prefix; the format is 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse

// il-superobj: super whose immediate super is kotlin.Any must reach the System.Object slot NON-virtually.
class ClrObjectModelNode(val id: Int) {
    override fun toString(): String = "N:" + super.toString().substring(0, 0) + id   // super -> System.Object::ToString
    override fun hashCode(): Int = super.hashCode()                                   // super -> System.Object::GetHashCode
    override fun equals(other: Any?): Boolean = super.equals(other)                   // super -> System.Object::Equals (identity)
}

// il-overridemsg: `override val message` on a class extending the @ClrTypeAlias base kotlin.Exception (-> System.Exception).
class ClrObjectModelMyEx : Exception("boom") {
    override val message: String get() = "overridden"
}

// Exception.InnerException is non-virtual: this remains a Kotlin virtual newslot and must not receive a CLR .override.
class ClrObjectModelCauseEx(private val ownCause: Throwable) : Exception("outer", IllegalStateException("base")) {
    override val cause: Throwable get() = ownCause
}

// il-pairtostr: a same-module user data class — its auto-toString embeds the (prefixed) class name and named fields.
data class ClrObjectModelRec(val name: String, val n: Int)

class ClrObjectModelTests {
    // il-samcmp: an explicit Comparator{} SAM literal drives sortWith both ascending and descending.
    @TestAttribute
    fun samComparatorLiteral() {
        val ns = mutableListOf(3, 1, 4, 1, 5, 9, 2, 6)
        ns.sortWith(Comparator { a, b -> a - b })
        assertEquals("1,1,2,3,4,5,6,9", ns.joinToString(","))   // 1,1,2,3,4,5,6,9
        ns.sortWith(Comparator { a, b -> b - a })
        assertEquals("9,6,5,4,3,2,1,1", ns.joinToString(","))   // 9,6,5,4,3,2,1,1
    }

    // il-superobj: super.toString/hashCode/equals to the System.Object slot (non-virtual base dispatch, no recursion).
    @TestAttribute
    fun superToObjectSlot() {
        val a = ClrObjectModelNode(7); val b = ClrObjectModelNode(7)
        assertEquals("N:7", a.toString())            // N:7  (super.toString() = type name, substring(0,0) = "")
        assertTrue(a.hashCode() == a.hashCode())     // True (stable identity hash; no recursion)
        assertTrue(a.equals(a))                      // True (reference identity via base Object.Equals)
        assertFalse(a.equals(b))                     // False (distinct instances)
    }

    // il-overridemsg: the override is DISPATCHED through the System.Exception.get_Message slot on every read path.
    @TestAttribute
    fun overrideExceptionMessage() {
        val e = ClrObjectModelMyEx()
        assertEquals("overridden", e.message)        // overridden — direct receiver
        val base: Exception = e                      // through the @ClrTypeAlias base static type -> virtual dispatch on the BCL slot
        assertEquals("overridden", base.message)     // overridden
        val caught = try {
            throw ClrObjectModelMyEx()                      // the throw/catch path reads System.Exception.Message
        } catch (ex: Exception) {
            ex.message
        }
        assertEquals("overridden", caught)           // overridden
    }

    @TestAttribute
    fun overrideNonVirtualExceptionCause() {
        val e = ClrObjectModelCauseEx(IllegalArgumentException("own"))
        assertEquals("own", e.cause!!.message)
    }

    // il-pairtostr: set/Triple/data-class toString routing (only the assertions NOT already covered elsewhere).
    @TestAttribute
    fun setTripleDataClassToString() {
        assertEquals("[1, 2, 3]", setOf(1, 2, 3).toString())              // [1, 2, 3]  (collection-style, C11)
        assertEquals("(1, 2, 3)", Triple(1, 2, 3).toString())            // (1, 2, 3)  (tuple)
        assertEquals("ClrObjectModelRec(name=k, n=9)", ClrObjectModelRec("k", 9).toString())  // Rec(name=k, n=9) with the ClrObjectModel prefix
    }
}
