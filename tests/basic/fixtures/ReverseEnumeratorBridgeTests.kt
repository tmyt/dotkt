// The REVERSE enumerator bridge (#139/#400): a Kotlin `Iterable` implementer seen through the CLR enumerable faces.
//
// A Kotlin class implementing `Iterable`/`Collection`/`List` lowers onto a BCL enumerable interface, which obliges
// `IEnumerator<E> GetEnumerator()` — but the class only has Kotlin's `iterator(): Iterator<E>`. bir2cir authors both
// halves as ordinary CIR: a compiler-owned `dotkt$EnumeratorOverKotlinIterator<T>` adapter, plus `GetEnumerator()`
// and the private non-generic `dotkt$NonGenericGetEnumerator()` with their exact MethodImpl descriptors.
//
// Both CLR faces are asserted separately, because they are two distinct slots that a single Kotlin declaration can
// never fill on its own:
//   * the GENERIC face — reached by every compiled stdlib body, which sees `Iterable<T>` as `IEnumerable<T>`;
//   * the NON-GENERIC face — `System.Collections.IEnumerable.GetEnumerator()`, walked here through the raw
//     `System.Collections.IEnumerator` protocol exactly as a pre-generics .NET consumer would.
//
// Assembly-wide collision rule: every top-level helper here is `RevBridge`-prefixed.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.Collections.IEnumerable as RevBridgeRawEnumerable

// A GENERIC owner: the bridge's `GetEnumerator` is declared on the open definition and every use of it here is a
// CONSTRUCTED instantiation, so the adapter must be constructed at the instantiation's element too.
class RevBridgeBox<T>(private val items: List<T>) : Iterable<T> {
    override fun iterator(): Iterator<T> = items.iterator()
}

// The face arrives through an abstract BASE; only the subclass declares `iterator()`, so only the subclass is owed
// the bridge and the base must not carry one.
abstract class RevBridgeBase : Iterable<String>

class RevBridgeWords(private val items: List<String>) : RevBridgeBase() {
    override fun iterator(): Iterator<String> = items.iterator()
}

// A narrowed iterator return: `MutableIterable.iterator()` states `MutableIterator<E>`, a SUBTYPE of `Iterator<E>`.
class RevBridgeMutableBag(private val backing: MutableList<Int>) : MutableIterable<Int> {
    override fun iterator(): MutableIterator<Int> = backing.iterator()
}

class ReverseEnumeratorBridgeTests {
    // The generic face at a constructed instantiation, through Kotlin `for`-in and through compiled stdlib bodies
    // (which see the receiver as `IEnumerable<E>` and therefore go through `GetEnumerator`).
    @TestAttribute
    fun genericFaceAtAConstructedInstantiation() {
        val ints = RevBridgeBox(listOf(1, 2, 3))
        var sum = 0
        for (x in ints) sum += x
        assertEquals(6, sum)
        assertEquals(3, ints.count())
        assertEquals(listOf(1, 2, 3), ints.toList())
        assertEquals("1-2-3", ints.joinToString("-"))

        // A SECOND instantiation of the same open definition, with a reference element.
        val strings = RevBridgeBox(listOf("a", "bb", "ccc"))
        assertEquals("a|bb|ccc", strings.joinToString("|"))
        assertEquals(3, strings.count())
    }

    // Two enumerations of the same instance must not share state: each `GetEnumerator` call wraps a fresh Kotlin
    // iterator, so a nested loop sees the full sequence on every pass.
    @TestAttribute
    fun eachEnumerationGetsItsOwnAdapter() {
        val ints = RevBridgeBox(listOf(1, 2, 3))
        var pairs = 0
        for (a in ints) for (b in ints) pairs++
        assertEquals(9, pairs)
    }

    // The face inherited through an abstract base, with only the subclass declaring `iterator()`.
    @TestAttribute
    fun faceInheritedThroughAnAbstractBase() {
        val words = RevBridgeWords(listOf("a", "bb", "ccc"))
        var total = 0
        for (w in words) total += w.length
        assertEquals(6, total)
        assertEquals(listOf("a", "bb", "ccc"), words.toList())
        val asBase: RevBridgeBase = words
        assertEquals(3, asBase.count())
    }

    // A `MutableIterable` implementer narrows the iterator return to `MutableIterator<E>`; the bridge still wraps it.
    @TestAttribute
    fun narrowedIteratorReturnStillBridges() {
        val bag = RevBridgeMutableBag(mutableListOf(4, 5, 6))
        var sum = 0
        for (x in bag) sum += x
        assertEquals(15, sum)
        assertEquals(3, bag.count())
    }

    // The NON-GENERIC face: `System.Collections.IEnumerable.GetEnumerator()` and the raw `IEnumerator` protocol.
    // The static type has to be laundered through `Any` because Kotlin's own type system does not relate `Iterable<T>`
    // to the CLR interface its lowering states; the emitted type does implement it, which is the point of the slot.
    @TestAttribute
    fun nonGenericFaceWalksTheRawEnumeratorProtocol() {
        val ints: Any = RevBridgeBox(listOf(1, 2, 3))
        val raw = ints as RevBridgeRawEnumerable
        val cursor = raw.GetEnumerator()
        var count = 0
        var sum = 0
        while (cursor.MoveNext()) {
            count++
            sum += cursor.Current as Int
        }
        assertEquals(3, count)
        assertEquals(6, sum)

        // The same slot on the class that inherits its face from a base.
        val words: Any = RevBridgeWords(listOf("a", "bb"))
        val wordCursor = (words as RevBridgeRawEnumerable).GetEnumerator()
        var joined = ""
        while (wordCursor.MoveNext()) joined += wordCursor.Current as String
        assertEquals("abb", joined)
    }

    // The non-generic face is independent of the generic one: enumerating through each in turn yields the same
    // sequence, so neither slot is wired to the other's state.
    @TestAttribute
    fun bothFacesEnumerateTheSameSequence() {
        val box = RevBridgeBox(listOf(7, 8))
        val viaGeneric = box.toList()
        val cursor = (box as Any as RevBridgeRawEnumerable).GetEnumerator()
        val viaRaw = mutableListOf<Int>()
        while (cursor.MoveNext()) viaRaw.add(cursor.Current as Int)
        assertEquals(viaGeneric, viaRaw.toList())
        assertTrue(viaGeneric.size == 2)
    }
}
