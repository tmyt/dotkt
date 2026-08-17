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

// A same-named OVERLOAD does not occupy the `GetEnumerator()` CLR signature the enumerable face demands, so the
// bridge is still owed. (`GetEnumerator(Int)` and `GetEnumerator()` are two distinct MethodDefs.)
class RevBridgeOverloadedName(private val items: List<Int>) : Iterable<Int> {
    override fun iterator(): Iterator<Int> = items.iterator()
    fun GetEnumerator(seed: Int): Int = seed + items.size
}

// A nullary `GetEnumerator()` of the author's own DOES occupy that signature, whatever it returns. The bridge then
// takes a collision-free physical name and is bound by its MethodImpl descriptor alone, so the CLR slot is still
// filled and the author's member keeps the public spelling.
class RevBridgeOwnGetEnumerator(private val items: List<Int>) : Iterable<Int> {
    override fun iterator(): Iterator<Int> = items.iterator()
    fun GetEnumerator(): Int = items.size
}

// The iterator implementation may come from a base that is not itself enumerable. The derived class is the first
// type that owes the CLR enumerable face, so it must receive a bridge even though it declares no iterator MethodDef.
open class RevBridgeIteratorProvider(protected val inheritedItems: List<String>) {
    open operator fun iterator(): Iterator<String> = inheritedItems.iterator()
}

class RevBridgeInheritedIterator(items: List<String>) : RevBridgeIteratorProvider(items), Iterable<String>

// The same obligation when the body comes from an interface default rather than a class base.
interface RevBridgeIteratorDefault {
    operator fun iterator(): Iterator<Int> = listOf(2, 4, 6).iterator()
}

class RevBridgeDefaultIterator : RevBridgeIteratorDefault, Iterable<Int>

// Kotlin's covariant Iterator return permits a narrower element than the Iterable face. The bridge must preserve
// the actual iterator element while presenting Current at the face's wider element type.
open class RevBridgeElement(val value: Int)
class RevBridgeNarrowElement(value: Int) : RevBridgeElement(value)

class RevBridgeNarrowedElement : Iterable<RevBridgeElement> {
    override fun iterator(): Iterator<RevBridgeNarrowElement> =
        listOf(RevBridgeNarrowElement(3), RevBridgeNarrowElement(5), RevBridgeNarrowElement(7)).iterator()
}

// The provider return need not state Iterator<E> directly. Primitive iterators are non-generic subtypes whose E is
// present only on their inherited Iterator face.
class RevBridgePrimitiveIterator : Iterable<Int> {
    override fun iterator(): IntIterator = object : IntIterator() {
        private var next = 1
        override fun hasNext(): Boolean = next <= 3
        override fun nextInt(): Int = next++
    }
}

// A user-defined non-generic cursor likewise carries E only on its inherited Iterator face.
class RevBridgeStringCursor(private val values: List<String>) : Iterator<String> {
    private var index: Int = 0
    override fun hasNext(): Boolean = index < values.size
    override fun next(): String = values[index++]
}

class RevBridgeCustomCursor : Iterable<String> {
    override fun iterator(): RevBridgeStringCursor = RevBridgeStringCursor(listOf("a", "bb", "ccc"))
}

// A private base declaration is not inherited. It must not hide the selected public interface default from the
// bridge provider search.
open class RevBridgePrivateIteratorBase {
    private operator fun iterator(): Iterator<Int> = listOf(99).iterator()
}

class RevBridgeDefaultOverPrivateBase : RevBridgePrivateIteratorBase(), RevBridgeIteratorDefault, Iterable<Int>

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

    // A same-named overload must not suppress the bridge: the type still needs the nullary `GetEnumerator()` slot,
    // and the author's own overload keeps working.
    @TestAttribute
    fun aSameNamedOverloadDoesNotSuppressTheBridge() {
        val bag = RevBridgeOverloadedName(listOf(1, 2, 3))
        assertEquals(listOf(1, 2, 3), bag.toList())
        assertEquals(3, bag.count())
        assertEquals(6, bag.GetEnumerator(3))   // the author's own overload: seed + size
        var sum = 0
        for (x in bag) sum += x
        assertEquals(6, sum)
    }

    // The author's own nullary `GetEnumerator()` occupies the physical signature; the bridge takes another name and
    // the CLR enumerable face still works, while the author's member still answers its own call.
    @TestAttribute
    fun anOwnNullaryGetEnumeratorStillLeavesTheSlotFilled() {
        val bag = RevBridgeOwnGetEnumerator(listOf(4, 5))
        assertEquals(2, bag.GetEnumerator())
        assertEquals(listOf(4, 5), bag.toList())
        assertEquals(9, bag.sum())
        val raw = (bag as Any as RevBridgeRawEnumerable).GetEnumerator()
        var seen = 0
        while (raw.MoveNext()) seen++
        assertEquals(2, seen)
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

    @TestAttribute
    fun iteratorInheritedFromANonEnumerableBaseStillBridges() {
        val values = RevBridgeInheritedIterator(listOf("a", "bb", "ccc"))
        assertEquals("a|bb|ccc", values.joinToString("|"))
        assertEquals(6, values.sumOf { it.length })
    }

    @TestAttribute
    fun iteratorInheritedFromAnInterfaceDefaultStillBridges() {
        val values = RevBridgeDefaultIterator()
        assertEquals(listOf(2, 4, 6), values.toList())
        assertEquals(12, values.sum())
    }

    @TestAttribute
    fun narrowerIteratorElementIsAdaptedToTheEnumerableFace() {
        val values: Iterable<RevBridgeElement> = RevBridgeNarrowedElement()
        assertEquals("3,5,7", values.joinToString(",") { it.value.toString() })
        val cursor = (values as Any as RevBridgeRawEnumerable).GetEnumerator()
        var sum = 0
        while (cursor.MoveNext()) sum += (cursor.Current as RevBridgeElement).value
        assertEquals(15, sum)
    }

    @TestAttribute
    fun primitiveIteratorSubtypeSuppliesItsInheritedElement() {
        val values = RevBridgePrimitiveIterator()
        assertEquals(listOf(1, 2, 3), values.toList())
        assertEquals(6, values.sum())
    }

    @TestAttribute
    fun customIteratorSubtypeSuppliesTheElementFromItsIteratorFace() {
        val values = RevBridgeCustomCursor()
        assertEquals("a|bb|ccc", values.joinToString("|"))
        assertEquals(6, values.sumOf { it.length })
    }

    @TestAttribute
    fun privateBaseMemberDoesNotSuppressTheInterfaceDefault() {
        val values = RevBridgeDefaultOverPrivateBase()
        assertEquals(listOf(2, 4, 6), values.toList())
        assertEquals(12, values.sum())
    }
}
