// A constructor argument the CLR mapping maps AWAY is still EVALUATED (#278).
//
// Some Kotlin collection constructors have no CLR counterpart for one of their parameters: the JVM hashtable
// `loadFactor` of `HashSet(initialCapacity, loadFactor)` names a concept `System.Collections.Generic.HashSet<T>`
// does not have, so bir2cir maps the call onto the capacity-only BCL constructor. Discarding the load-factor VALUE
// is the mapping; discarding its EVALUATION is a miscompile — Kotlin evaluates every argument expression a call
// supplies exactly once, in argument order, whatever the emitted CLR call shape has slots for. Before the fix
// `HashSet(16, computeLoadFactor())` never called `computeLoadFactor()`, and an exception it would have thrown
// simply never happened.
//
// The rule under test is about mapped-away PARAMETERS, not about HashSet: every constructor the same mapping table
// covers is asserted here — HashSet, HashMap (Dictionary) and LinkedHashMap (OrderedDictionary) all lose the
// load-factor slot — alongside LinkedHashSet, which is a real Kotlin class whose constructor keeps both arguments
// and must be unaffected. Every side effect is captured into `mcaLog` and asserted positionally, which is stronger
// than a value assertion: a mapped-away argument evaluated zero or twice only shows up in the log.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

val mcaLog = mutableListOf<String>()

/** Records its tag and returns a capacity — an initialCapacity with an OBSERVABLE evaluation. */
fun mcaCap(tag: String): Int { mcaLog.add("cap$tag"); return 8 }

/** Records its tag and returns a load factor — the argument the mapping discards. */
fun mcaLf(tag: String): Float { mcaLog.add("lf$tag"); return 0.75f }

/** A mapped-away argument that THROWS: its evaluation is observable by not returning at all. */
fun mcaBoom(): Float = throw IllegalStateException("boom")

private fun mcaTrace(): String = mcaLog.joinToString(",")

/** The mapped construction as a constructor-delegation argument — the bindings become the ctor's pre-statements. */
class McaHolder(val set: HashSet<Int>) {
    constructor(tag: String) : this(HashSet<Int>(mcaCap(tag), mcaLf(tag)))
}

/** …and as a property initializer, where the block the bindings live in is the initializer's own. */
class McaFieldOwner {
    val map: HashMap<Int, Int> = HashMap(mcaCap("F"), mcaLf("F"))
}

/** Two operands, so the mapped construction sits in a NON-FIRST slot with the first already on the stack. */
fun mcaPair(a: Any, b: Any): String = "$a/$b"

class MappedConstructorArgumentTests {
    /** HashSet: the mapped-away load factor runs, after the capacity it is written after. */
    @TestAttribute
    fun hashSetEvaluatesTheMappedAwayLoadFactor() {
        mcaLog.clear()
        val s = HashSet<Int>(mcaCap("A"), mcaLf("A"))
        s.add(1); s.add(2); s.add(2)
        assertEquals(2, s.size)
        assertEquals("capA,lfA", mcaTrace())   // was "capA": the load factor was dropped unevaluated
    }

    /** HashMap -> Dictionary: the same table entry, the same rule. */
    @TestAttribute
    fun hashMapEvaluatesTheMappedAwayLoadFactor() {
        mcaLog.clear()
        val m = HashMap<Int, String>(mcaCap("B"), mcaLf("B"))
        m[1] = "x"
        assertEquals(1, m.size)
        assertEquals("capB,lfB", mcaTrace())   // was "capB"
    }

    /** LinkedHashMap -> OrderedDictionary: likewise. */
    @TestAttribute
    fun linkedHashMapEvaluatesTheMappedAwayLoadFactor() {
        mcaLog.clear()
        val m = LinkedHashMap<Int, Int>(mcaCap("C"), mcaLf("C"))
        m[1] = 10
        assertEquals(1, m.size)
        assertEquals("capC,lfC", mcaTrace())   // was "capC"
    }

    /** LinkedHashSet is a real Kotlin class — no mapping applies, and both arguments were always evaluated. */
    @TestAttribute
    fun unmappedConstructorIsUnchanged() {
        mcaLog.clear()
        val s = LinkedHashSet<Int>(mcaCap("D"), mcaLf("D"))
        s.add(1)
        assertEquals(1, s.size)
        assertEquals("capD,lfD", mcaTrace())
    }

    /** A CONST load factor is unobservable, so it is dropped rather than evaluated into a local nobody reads —
     *  and the surviving capacity then stays in its own slot. The common `HashSet(16, 0.75f)` idiom pays nothing. */
    @TestAttribute
    fun constantLoadFactorLeavesTheCapacityInline() {
        mcaLog.clear()
        val s = HashSet<Int>(mcaCap("E"), 0.75f)
        s.add(1)
        assertEquals(1, s.size)
        assertEquals("capE", mcaTrace())
    }

    /** …and a CONST capacity is a literal push, so nothing observable orders against the mapped-away argument. */
    @TestAttribute
    fun constantCapacityStillEvaluatesTheLoadFactor() {
        mcaLog.clear()
        val s = HashSet<Int>(16, mcaLf("G"))
        s.add(1)
        assertEquals(1, s.size)
        assertEquals("lfG", mcaTrace())
    }

    /** An exception from the mapped-away argument reaches the caller: the construction never happens. */
    @TestAttribute
    fun throwingMappedAwayArgumentStillThrows() {
        mcaLog.clear()
        val outcome = try {
            HashSet<Int>(mcaCap("H"), mcaBoom())
            "constructed"
        } catch (e: IllegalStateException) {
            e.message ?: "?"
        }
        assertEquals("boom", outcome)          // was "constructed": the throw was compiled away
        assertEquals("capH", mcaTrace())
    }

    /** The mapped construction in a constructor-delegation argument evaluates both, in order, once. */
    @TestAttribute
    fun delegationArgumentEvaluatesBoth() {
        mcaLog.clear()
        val h = McaHolder("I")
        assertEquals(0, h.set.size)
        assertEquals("capI,lfI", mcaTrace())
    }

    /** …and in a property initializer. */
    @TestAttribute
    fun propertyInitializerEvaluatesBoth() {
        mcaLog.clear()
        val owner = McaFieldOwner()
        assertEquals(0, owner.map.size)
        assertEquals("capF,lfF", mcaTrace())
    }

    /** The construction in a NON-FIRST operand slot: the statements the mapping emits run with the first operand
     *  already on the evaluation stack. Harmless in itself — but a `try` among them would enter a protected region
     *  with a non-empty stack, which the CLR refuses, so this pins that the hoist sees inside the minted block. */
    @TestAttribute
    fun mappedConstructionInALaterOperandSlot() {
        mcaLog.clear()
        val r = mcaPair("x", HashSet<Int>(try { 16 } catch (e: Exception) { 8 }, mcaLf("K")).size)
        assertEquals("x/0", r)
        assertEquals("lfK", mcaTrace())
    }

    /** EXACTLY once per construction: a lambda invoked twice evaluates both arguments twice, in order. */
    @TestAttribute
    fun eachConstructionEvaluatesItsArgumentsOnce() {
        mcaLog.clear()
        val make = { LinkedHashMap<Int, Int>(mcaCap("J"), mcaLf("J")) }
        make()
        make()
        assertEquals("capJ,lfJ,capJ,lfJ", mcaTrace())
    }
}
