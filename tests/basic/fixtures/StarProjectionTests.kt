// STAR PROJECTION — a slot that abandons a type argument (#368).
//
// THE RULE this battery pins: wherever the set of instantiations Kotlin's subtyping admits for a slot differs from
// the set its physical CLR type admits under assignment compatibility, the lowering is wrong. `List<*>` admits
// `List<Int>`; the reified `IReadOnlyList<object>` does not (ECMA-335 §I.8.7.1: a value type reaches `object` only
// by BOXING, never by a reference conversion), so lowering the projection to it makes every member call on the slot
// fault at run time — EntryPointNotFound for a collection, an InvalidCast for the invariant `IList<object>` form,
// and, for `Array<*>` read as `object[]`, an AccessViolation that aborts the process because that is a raw
// reinterpret of element storage rather than a failed cast.
//
// THE DISCRIMINATOR IS THE ELEMENT TYPE, NOT THE SPELLING. Every case below is asserted for a VALUE element and a
// REFERENCE element. A reference-element collection passes even under the broken lowering (covariance works for
// references), so a fixture that tested only `List<String>` would be green against the bug. The value-element half
// is what discriminates; the reference-element half is what proves the fix did not trade one for the other.
//
// `List<Any?>` / `List<Any>` / `List<out Any?>` are NOT here: for a covariant parameter Kotlin admits exactly the
// same instantiations as `<*>` does, so they carry the same fault, but closing them needs the declaration-site
// variance of a @ClrTypeAlias'd type, which no artifact bir2cir reads carries. Tracked separately.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse

// ---- receivers whose declared slot abandons the argument -------------------------------------------------------
fun starListSize(l: List<*>): Int = l.size
fun starListEmpty(l: List<*>): Boolean = l.isEmpty()
fun starListGet(l: List<*>, i: Int): Any? = l[i]
fun starListContains(l: List<*>, x: Any?): Boolean = l.contains(x)
fun starListIndexOf(l: List<*>, x: Any?): Int = l.indexOf(x)
fun starListFirst(l: List<*>): Any? { for (x in l) return x; return null }

fun starCollSize(c: Collection<*>): Int = c.size
fun starCollEmpty(c: Collection<*>): Boolean = c.isEmpty()
fun starCollFirst(c: Collection<*>): Any? { for (x in c) return x; return null }
fun starSetSize(s: Set<*>): Int = s.size
fun starIterableFirst(i: Iterable<*>): Any? { for (x in i) return x; return null }
fun starSequenceFirst(s: Sequence<*>): Any? { for (x in s) return x; return null }

fun starMutListSize(l: MutableList<*>): Int = l.size
fun starMutListEmpty(l: MutableList<*>): Boolean = l.isEmpty()
fun starMutListRemoveAt(l: MutableList<*>, i: Int): Any? = l.removeAt(i)

fun starMapSize(m: Map<*, *>): Int = m.size
fun starMapEmpty(m: Map<*, *>): Boolean = m.isEmpty()
fun starMapGet(m: Map<*, *>, k: Any?): Any? = m.get(k)
fun starMapHasKey(m: Map<*, *>, k: Any?): Boolean = m.containsKey(k)
fun starMapKeyCount(m: Map<*, *>): Int = m.keys.size
fun starMapKnownKeySize(m: Map<String, *>): Int = m.size      // ONE argument abandoned, not both
fun starMapKnownValSize(m: Map<*, Int>): Int = m.size

fun starArraySize(a: Array<*>): Int = a.size
fun starArrayGet(a: Array<*>, i: Int): Any? = a[i]
fun starArrayFirst(a: Array<*>): Any? { for (x in a) return x; return null }

// The generic-callee seam: the type argument inferred from a captured projection is `Any?`, so the callee's
// parameter is a reified construction at `object` which the value does not inhabit.
fun starListFirstOrNull(l: List<*>): Any? = l.firstOrNull()
fun starListCount(l: List<*>): Int = l.count()
fun starListJoin(l: List<*>): String = l.joinToString(",")
fun starArrayFirstOrNull(a: Array<*>): Any? = a.firstOrNull()

// ---- DotKt generics: the existential-view provider --------------------------------------------------------------
class StarBox<T>(val v: T) {
    fun raw(): Any? = v
    fun describe(): String = "box"
}

// Arity > 1. The old lowering refused to give a multi-parameter generic an existential view at all, and the
// resulting call read a DIFFERENT field — a silent wrong answer, not a fault.
class StarDuo<A, B>(val a: A, val b: B) {
    fun first(): Any? = a
    fun second(): Any? = b
}

fun starBoxRaw(b: StarBox<*>): Any? = b.raw()
fun starBoxField(b: StarBox<*>): Any? = b.v
fun starDuoFirst(d: StarDuo<*, *>): Any? = d.first()
fun starDuoSecond(d: StarDuo<*, *>): Any? = d.second()
fun starDuoPartialFirst(d: StarDuo<Int, *>): Any? = d.first()   // one argument still known

class StarProjectionTests {
    // FAMILY A — the BCL-alias collections. Value elements are the discriminator: under the old lowering every
    // `Int`-elemented row threw EntryPointNotFoundException at `IReadOnlyCollection<object>::get_Count`.
    @TestAttribute
    fun starProjectedListMembers() {
        val vi: List<Int> = listOf(10, 20, 30)
        val vs: List<String> = listOf("a", "b")
        assertEquals(3, starListSize(vi))
        assertEquals(2, starListSize(vs))
        assertFalse(starListEmpty(vi))
        assertTrue(starListEmpty(listOf<Int>()))
        assertEquals(20, starListGet(vi, 1))
        assertEquals("b", starListGet(vs, 1))
        assertTrue(starListContains(vi, 20))
        assertFalse(starListContains(vi, 99))
        assertEquals(2, starListIndexOf(vi, 30))
        assertEquals(10, starListFirst(vi))
        assertEquals("a", starListFirst(vs))
    }

    // `Collection<*>`, `Set<*>`, `Iterable<*>`, `Sequence<*>` — the same alias family through their other faces.
    // The `setOf(1)` rows matter on their own: a `HashSet<T>` implements NO non-generic collection interface
    // beyond IEnumerable, so a view that named `ICollection` would pass every list row and fail these.
    @TestAttribute
    fun starProjectedCollectionFaces() {
        assertEquals(3, starCollSize(listOf(1, 2, 3)))
        assertEquals(2, starCollSize(listOf("a", "b")))
        assertEquals(2, starCollSize(setOf(1, 2)))
        assertFalse(starCollEmpty(setOf(1, 2)))
        assertTrue(starCollEmpty(setOf<Int>()))
        assertEquals(1, starCollFirst(listOf(1, 2)))
        assertEquals(2, starSetSize(setOf(1, 2)))
        assertEquals(1, starSetSize(setOf("a")))
        assertEquals(1, starIterableFirst(listOf(1, 2)))
        assertEquals("a", starIterableFirst(listOf("a")))
        assertEquals(1, starSequenceFirst(sequenceOf(1, 2)))
        assertEquals("a", starSequenceFirst(sequenceOf("a")))
    }

    // FAMILY A' — the INVARIANT alias. `MutableList<*>` lowered to `IList<object>`, which fails for BOTH element
    // kinds (invariance admits no covariance rescue at all), so this row discriminates where the read-only ones
    // could still pass on a reference element.
    @TestAttribute
    fun starProjectedMutableList() {
        val vi: MutableList<Int> = mutableListOf(1, 2, 3)
        val vs: MutableList<String> = mutableListOf("a")
        assertEquals(3, starMutListSize(vi))
        assertEquals(1, starMutListSize(vs))
        assertFalse(starMutListEmpty(vi))
        assertTrue(starMutListEmpty(mutableListOf<String>()))
        assertEquals(2, starMutListRemoveAt(vi, 1))
        assertEquals(2, starMutListSize(vi))          // the removal went through to the real list, not a copy
    }

    // FAMILY A'' — maps, including the PARTIAL projections. `Map<String,*>` admits `Map<String,Int>` exactly as
    // `Map<*,*>` does, so one abandoned argument is as fatal as two; these ran correctly before only through an
    // unverifiable IL path that ilverify rejects.
    @TestAttribute
    fun starProjectedMap() {
        val vi: Map<Int, Int> = mapOf(1 to 2, 3 to 4)
        val vs: Map<String, String> = mapOf("a" to "b")
        assertEquals(2, starMapSize(vi))
        assertEquals(1, starMapSize(vs))
        assertFalse(starMapEmpty(vi))
        assertTrue(starMapEmpty(mapOf<Int, Int>()))
        assertEquals(2, starMapGet(vi, 1))
        assertEquals("b", starMapGet(vs, "a"))
        assertTrue(starMapHasKey(vi, 3))
        assertFalse(starMapHasKey(vi, 9))
        assertEquals(2, starMapKeyCount(vi))
        assertEquals(1, starMapKnownKeySize(mapOf("a" to 1)))
        assertEquals(2, starMapKnownValSize(vi))
    }

    // FAMILY B — arrays. `Array<*>` lowered to `object[]`, and reading an `int32[]` through it is a raw
    // reinterpret: the measured symptom was an AccessViolation that killed the process, so a value-element row
    // here is the difference between a test failure and no test run at all.
    @TestAttribute
    fun starProjectedArray() {
        val ai: Array<Int> = arrayOf(1, 2, 3)
        val asr: Array<String> = arrayOf("a", "b")
        assertEquals(3, starArraySize(ai))
        assertEquals(2, starArraySize(asr))
        assertEquals(2, starArrayGet(ai, 1))
        assertEquals("b", starArrayGet(asr, 1))
        assertEquals(1, starArrayFirst(ai))
        assertEquals("a", starArrayFirst(asr))
    }

    // The GENERIC-CALLEE seam. Distinct from every case above: the fault is at the call BOUNDARY (the callee's
    // parameter is a reified construction at `object`), not at a member dispatch on the slot, so it survives a fix
    // that only corrects the slot's own type.
    @TestAttribute
    fun starProjectedGenericCallee() {
        assertEquals(1, starListFirstOrNull(listOf(1, 2)))
        assertEquals("a", starListFirstOrNull(listOf("a")))
        assertEquals(3, starListCount(listOf(1, 2, 3)))
        assertEquals("1,2", starListJoin(listOf(1, 2)))
        assertEquals(1, starArrayFirstOrNull(arrayOf(1, 2)))
        assertEquals("a", starArrayFirstOrNull(arrayOf("a")))
    }

    // FAMILY C — a DotKt generic reaches its existential view. Arity is irrelevant to the rule, and it used to be
    // the gate: a two-parameter generic got no view, and `StarDuo<Int,String>.first()` returned the value of
    // `second` — a silently WRONG answer rather than a fault, which is why the value/reference pair here is
    // asserted against the field it names rather than against "no exception".
    @TestAttribute
    fun starProjectedUserGeneric() {
        assertEquals(7, starBoxRaw(StarBox(7)))
        assertEquals("s", starBoxRaw(StarBox("s")))
        assertEquals(9, starBoxField(StarBox(9)))
        assertEquals(3, starDuoFirst(StarDuo(3, "x")))
        assertEquals("x", starDuoSecond(StarDuo(3, "x")))
        assertEquals("l", starDuoFirst(StarDuo("l", 4)))
        assertEquals(4, starDuoSecond(StarDuo("l", 4)))
        assertEquals(5, starDuoPartialFirst(StarDuo(5, "y")))
    }
}
