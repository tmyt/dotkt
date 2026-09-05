// Generics battery — migrates cases/il-generic .. il-generic6 (the G-1..G-6 progressive-milestone cases the
// audit §5.3 condemns as 6 permanent compiler processes). Here they are 6 @TestAttribute methods in ONE
// fixture, compiled ONCE into this assembly. Same plain-Kotlin, same oracle, same compile conditions — the
// old stdout diff (print each computed value, compare to a hardcoded golden string) becomes a per-value
// assertEquals, which is strictly stronger (a regression fails the exact broken contract, typed diff) and
// self-documenting. Every value the old il_check asserted is preserved 1:1 (see the // <expected> comments).
//
// FIRST migrated family = the reusable template. Coverage preserved:
//   g1 generic class + generic function       (was il-generic)
//   g2 generic INTERFACE + constructed impls   (was il-generic2)
//   g3 bounded type param <T:Comparable<T>>    (was il-generic3)
//   g4 generic method on a generic class       (was il-generic4)
//   g5 generic indexer (operator get/set)      (was il-generic5)
//   g6 declaration-site variance (out/in)      (was il-generic6)
import NUnit.Framework.TestAttribute
// Standard assertion imports (the convention for every battery — see docs/design-nunit-test-harness.md).
// NUnit's static asserts remain direct KLIB static declarations; `import ... as` aliases the member as a callable
// so tests read idiomatically.
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

// G-1: generic class + generic function.
class Box<T>(val value: T) { fun get(): T = value }
fun <T> identity(x: T): T = x
class Pair2<A, B>(val first: A, val second: B) {
    fun firstOne(): A = first
    fun secondOne(): B = second
}

// G-2: generic INTERFACE + classes implementing a constructed instantiation.
interface Container<T> {
    fun item(): T
    fun describe(): String
}
class IntBox(val n: Int) : Container<Int> {
    override fun item(): Int = n
    override fun describe(): String = "IntBox holding an Int"
}
class Named(val label: String) : Container<String> {
    override fun item(): String = label
    override fun describe(): String = "Named holding a String"
}

// G-3: bounded type parameters <T : Comparable<T>> -> CLR generic constraint (IComparable<T>), so `a > b`
// (= a.compareTo(b) > 0) is callable on a bare T.
fun <T : Comparable<T>> maxOf2(a: T, b: T): T = if (a > b) a else b
class SortedPair<T : Comparable<T>>(val a: T, val b: T) { fun larger(): T = if (a > b) a else b }

// G-4: generic method on a generic class (generic-on-generic) + a generic method returning its type param.
class Holder<T>(val value: T) {
    fun <R> pairWith(other: R): String = "$value & $other"
    fun get(): T = value
}
fun <A, B> firstOf(a: A, b: B): A = a

// G-5: generic indexer (operator get/set on a constructed generic).
class Slot<T>(var a: T, var b: T) {
    operator fun get(i: Int): T = if (i == 0) a else b
    operator fun set(i: Int, v: T) { if (i == 0) a = v else b = v }
}

// G-6: declaration-site variance over reference types -> CLR covariant/contravariant interfaces.
// (Value-type args like Source<Int> do NOT covary on the CLR — a JVM-boxing artifact; reified generics keep
//  them distinct, matching C#. So variance is exercised for reference types.)
interface Producer<out T> { fun produce(): T }
interface Consumer<in T> { fun consume(t: T): String }
interface UnsafeProducer<out T> {
    fun roundTrip(value: @UnsafeVariance T): T
}
class HelloProducer : Producer<String> { override fun produce(): String = "hello" }
class IntProducer(private val value: Int) : Producer<Int> { override fun produce(): Int = value }
class AnyConsumer : Consumer<Any> { override fun consume(t: Any): String = "consumed: $t" }
class UnsafeStringProducer : UnsafeProducer<String> { override fun roundTrip(value: String): String = value }
class CovariantValue<out T>(val value: T)
class InvariantValue<T>(val value: T)
class ProjectedArrayHelper { fun <T> first(values: Array<out T>): T = values[0] }
fun useProducer(p: Producer<Any>): String = p.produce().toString()   // covariance: Producer<String> flows in
fun useConsumer(c: Consumer<String>): String = c.consume("world")    // contravariance: Consumer<Any> flows in

// Different closed constructions of a covariant E share one logical projected array, but cannot be stored in the
// CLR fiction `Producer<object>[]` (in particular Producer<Int> is not that type). Exercise heterogeneous allocation,
// a result-independent array helper, and projected reads through the exact Array<out E> declaration contract.
fun <T> projectedProducerValues(producers: Array<out Producer<T>>): String {
    if (producers.isEmpty()) return "empty"
    val first = producers[0].produce().toString()
    return first + ":" + producers[1].produce().toString()
}

fun exactCovariantProducerArray(): Array<Producer<Any>> = arrayOf(HelloProducer())

fun <T> projectedProducerFirst(producers: Array<out Producer<T>>): Producer<T> = producers[0]

fun <T> firstProjectedValue(values: Array<out T>): T = values[0]

fun <T> projectedProducerViaHelper(producers: Array<out Producer<T>>): String =
    firstProjectedValue(producers).produce().toString()

fun <T> projectedProducerViaMemberHelper(producers: Array<out Producer<T>>): String =
    ProjectedArrayHelper().first(producers).produce().toString()

fun <T, R> transformProjectedFirst(values: Array<out T>, transform: (T) -> R): R = transform(values[0])

class NumberComparable(private val label: String) : Comparable<Number> {
    override fun compareTo(other: Number): Int = 0
    override fun toString(): String = label
}
fun projectedComparableText(value: Comparable<in Number>): String = value.toString()

fun <T> projectedProducerViaCallback(producers: Array<out Producer<T>>): String =
    transformProjectedFirst(producers) { it.produce().toString() }

fun localProjectedProducers(): String {
    val producers = arrayOf(IntProducer(5), HelloProducer())
    return projectedProducerValues(producers)
}

fun initializedProjectedProducerArray(): Array<Producer<Any>> {
    val captured = 8
    return Array(2) { index -> if (index == 0) IntProducer(captured) else HelloProducer() }
}

fun covariantClassArray(): Array<CovariantValue<Any>> =
    arrayOf<CovariantValue<Any>>(CovariantValue("class"))

fun charSequenceProducerArray(): Array<Producer<CharSequence>> =
    arrayOf<Producer<CharSequence>>(HelloProducer())

fun unsafeVarianceProducerArray(): Array<UnsafeProducer<Any>> =
    arrayOf<UnsafeProducer<Any>>(UnsafeStringProducer())

fun invariantProjectedValue(values: Array<out InvariantValue<String>>): String = values[0].value

fun spreadProjectedProducerArray(): Array<Producer<Any>> =
    arrayOf(*arrayOf(IntProducer(10)), *arrayOf(HelloProducer()))

fun spreadProjectedProducerInputs(
    ints: Array<IntProducer>,
    strings: Array<HelloProducer>,
): Array<Producer<Any>> = arrayOf(*ints, *strings)

private interface PrivateProducer<out T> { fun producePrivate(): T }
private class PrivateIntProducer : PrivateProducer<Int> { override fun producePrivate(): Int = 9 }
private fun <T> nullableProjectedProducer(values: Array<out PrivateProducer<T>?>): String =
    values[0]?.producePrivate().toString()

// G-7: a member called on a receiver whose STATIC TYPE IS THE TYPE PARAMETER. The stack holds a `!!T`, not an
// interface reference, so the only verifiable dispatch is `constrained. !!T ; callvirt` — for every spelling of
// the receiver (a parameter, a local copy, a field, a T-returning call result) and for a NON-generic constraint
// (`Tagged`) as much as a generic one (`Keyed<Int>`).
interface Tagged {
    fun tag(): Int
    fun retag(n: Int)
}

interface Keyed<K> {
    fun key(): K
}

class TaggedKeyed(var n: Int) : Tagged, Keyed<Int> {
    override fun tag(): Int = n
    override fun retag(n: Int) { this.n = n }
    override fun key(): Int = n * 10
}

fun <T : Tagged> tagOfParam(t: T): Int = t.tag()
fun <T : Tagged> tagOfLocal(t: T): Int { val copy = t; return copy.tag() }
fun <T : Keyed<Int>> keyOfParam(t: T): Int = t.key()
fun <T : Tagged> tagOfNotNull(t: T?): Int = t!!.tag()

class TaggedHolder<T : Tagged>(val item: T) {
    fun tagOfField(): Int = item.tag()
    fun tagOfCallResult(): Int = get().tag()
    fun get(): T = item
    // Property ACCESSOR bodies — executable code that lives under `properties`, not `methods`. The SETTER is a
    // separate body from the getter and reaches the emitter through its own walk.
    var accessorTag: Int
        get() = item.tag()
        set(value) { item.retag(value) }
}

// The accessor case again with an OVERLOADED member, where a mis-selected overload is a wrong VALUE.
class DescribedHolder<T : Described>(val item: T) {
    val accessorDescription: String get() = item.describe(4)
}

// Changing the DISPATCH must not change WHICH member is called, nor how it is instantiated: an OVERLOAD still
// has to be selected by signature (dispatching a `describe(Int)` call to `describe(String)` is a silent wrong
// answer, not a verifier complaint), and a GENERIC member still needs its instantiation.
interface Described {
    fun describe(x: Int): String
    fun describe(x: String): String
    fun <R> firstOf(a: R, b: R): R
}

class DescribedTag(var n: Int) : Tagged, Described {
    override fun tag(): Int = n
    override fun retag(n: Int) { this.n = n }
    override fun describe(x: Int): String = "int:$x"
    override fun describe(x: String): String = "str:$x"
    override fun <R> firstOf(a: R, b: R): R = a
}

fun <T : Described> describeOverloads(t: T): String = t.describe(7) + "/" + t.describe("s")
fun <T : Described> genericMemberOnTypeParam(t: T): String = t.firstOf("a", "b")

// The called member is declared on a GENERIC BASE of the bound, which the bound's own constraint list does not
// name — the constructed owner comes from the hierarchy substitution, not from the constraint.
interface RootProducer<X> {
    fun produceRoot(): X
}

interface LeafProducer<X> : RootProducer<X> {
    fun leaf(): Int
}

class IntLeaf : LeafProducer<Int> {
    override fun produceRoot(): Int = 55
    override fun leaf(): Int = 5
}

fun <T : LeafProducer<Int>> rootThroughBase(t: T): Int = t.produceRoot() + t.leaf()

// An open CLASS bound rather than an interface: a virtual and a non-virtual member on the same receiver.
open class TagBase {
    open fun openValue(): Int = 6
    fun finalValue(): Int = 60
}

class TagDerived : TagBase() {
    override fun openValue(): Int = 66
}

fun <T : TagBase> classBoundReceiver(t: T): Int = t.openValue() + t.finalValue()

// `!0` and `!1` are distinct positions in a CLR MethodDef signature. Collapsing both to one wildcard in the
// emitter's declaration index either rejects this legal overload set or silently binds one body to the other.
class TypeVariableOverloads<A, B> {
    fun choose(value: A): String = "first:$value"
    fun choose(value: B): String = "second:$value"
}

// Non-array use-site projections must remain semantic BIR facts until bir2cir selects their physical existential
// representation. Keep every declaration position in one compact family: invariant/variant owners, nested generic
// arguments, bounds, parameters/results, and function-type parameters/results.
interface UseSiteInvariant<T> {
    fun read(): T
    fun write(value: T)
}

private class UseSiteAnyBox(private var value: Any) : UseSiteInvariant<Any> {
    override fun read(): Any = value
    override fun write(value: Any) { this.value = value }
}

private class UseSiteStringBox(private var value: String) : UseSiteInvariant<String> {
    override fun read(): String = value
    override fun write(value: String) { this.value = value }
}

private class UseSiteProjectedConstructor<T> private constructor(
    val box: UseSiteInvariant<T>,
    val selected: String,
) {
    private constructor(box: UseSiteInvariant<T>) : this(box, "single")
    private constructor(box: UseSiteInvariant<T>, marker: Int) : this(box, "int:$marker")
    private constructor(box: UseSiteInvariant<T>, marker: Int?) : this(box, "nullable:$marker")
    private constructor(box: UseSiteInvariant<T>, tags: List<*>) : this(box, "star:${tags.size}")
    private constructor(box: UseSiteInvariant<T>, tags: Array<String>) : this(box, "array:${tags.size}")

    companion object {
        fun from(box: UseSiteInvariant<*>): UseSiteProjectedConstructor<*> = UseSiteProjectedConstructor(box)
        fun fromNullable(box: UseSiteInvariant<*>): UseSiteProjectedConstructor<*> {
            val marker: Int? = 7
            return UseSiteProjectedConstructor(box, marker)
        }
        fun fromStar(box: UseSiteInvariant<*>): UseSiteProjectedConstructor<*> =
            UseSiteProjectedConstructor(box, listOf("tag"))
        fun fromArray(box: UseSiteInvariant<*>): UseSiteProjectedConstructor<*> =
            UseSiteProjectedConstructor(box, arrayOf("tag"))
    }
}

private class UseSiteProjectedBoundOuter<T> {
    inner class Writer<C : MutableList<in T>>(private val destination: C) {
        fun insert(value: T): C {
            destination.add(0, value)
            return destination
        }
    }
}

// Constructor inference introduces a captured projection here. The allocation must use a constructible closed CLR
// type; the projected existential view is only the value-use representation and has no constructor of its own.
private fun constructFromUseSiteProjection(box: UseSiteInvariant<*>): UseSiteProjectedConstructor<*> =
    UseSiteProjectedConstructor.from(box)

fun useSiteInParameter(box: UseSiteInvariant<in String>) { box.write("in") }
fun useSiteOutResult(): UseSiteInvariant<out String> = UseSiteStringBox("out")
fun useSiteVariantParameter(producer: Producer<out String>): String = producer.produce()
fun useSiteVariantResult(): Consumer<in String> = AnyConsumer()
fun useSiteNested(values: List<UseSiteInvariant<out String>>): String = values[0].read()
fun <M : UseSiteInvariant<in String>> useSiteConstraint(box: M): M {
    box.write("bound")
    return box
}
fun <T, C : MutableList<in T>> useSiteProjectedListInsert(destination: C, value: T): C {
    destination.add(0, value)
    return destination
}
fun <T, C : MutableCollection<in T>> useSiteProjectedCollectionAdd(destination: C, value: T): C {
    destination.add(value)
    return destination
}
fun <T, C : MutableCollection<in T>> useSiteProjectedCollectionAddChanged(destination: C, value: T): Boolean =
    destination.add(value)
fun <T, C : MutableCollection<in T>?> useSiteNullableProjectedCollectionAdd(destination: C, value: T): Boolean =
    destination?.add(value) ?: false
fun <T, C : MutableCollection<in T>> useSiteProjectedCollectionAddCaptured(destination: C, value: T): C {
    val add = { destination.add(value) }
    add()
    return destination
}
fun useSiteDirectProjectedCollectionAdd(destination: MutableCollection<in String>, value: String): Boolean =
    destination.add(value)
fun <T, C : MutableList<in T>> useSiteProjectedListCollectionAdd(destination: C, value: T): Boolean =
    destination.add(value)
fun <C : MutableCollection<*>> useSiteStarProjectedCollectionAdd(destination: C, value: Nothing): Boolean =
    destination.add(value)
class UseSiteProjectedCollectionAppender<T, C : MutableCollection<in T>>(private val destination: C) {
    fun add(value: T): Boolean = destination.add(value)
}
fun <T, C : MutableCollection<in T>> useSiteProjectedCollectionIsEmpty(destination: C) = destination.isEmpty()
fun <T, C : MutableCollection<in T>> useSiteProjectedCollectionContains(destination: C, value: T) =
    destination.contains(value)
fun <T, C : MutableCollection<in T>> useSiteProjectedCollectionContainsAll(
    destination: C,
    values: Collection<T>,
) = destination.containsAll(values)
fun <T, C : MutableCollection<in T>> useSiteProjectedCollectionAddAll(destination: C, values: Collection<T>) =
    destination.addAll(values)
fun <T, C : MutableCollection<in T>> useSiteProjectedCollectionRemoveAll(destination: C, values: Collection<T>) =
    destination.removeAll(values)
fun <T, C : MutableCollection<in T>> useSiteProjectedCollectionRetainAll(destination: C, values: Collection<T>) =
    destination.retainAll(values)
fun <T, C : MutableList<in T>> useSiteProjectedListAddAllAt(destination: C, values: Collection<T>) =
    destination.addAll(0, values)
fun <T, C : MutableList<in T>> useSiteProjectedListSet(destination: C, value: T) = destination.set(0, value)
fun <T, C : MutableList<in T>> useSiteProjectedListRemoveAt(destination: C) = destination.removeAt(0)
fun <T, C : MutableList<in T>> useSiteProjectedListIndexOf(destination: C, value: T) = destination.indexOf(value)
fun <T, C : MutableList<in T>> useSiteProjectedListLastIndexOf(destination: C, value: T) =
    destination.lastIndexOf(value)
fun <T, C : MutableList<in T>> useSiteProjectedListIterator(destination: C) = destination.listIterator()
fun <T, C : MutableList<in T>> useSiteProjectedListIteratorAt(destination: C, index: Int) =
    destination.listIterator(index)
fun <T, C : MutableList<in T>> useSiteProjectedListSubList(destination: C) = destination.subList(0, 2)
fun <T> directProjectedCollectionIsEmpty(destination: MutableCollection<in T>) = destination.isEmpty()
fun <T> directProjectedCollectionContains(destination: MutableCollection<in T>, value: T) =
    destination.contains(value)
fun <T> directProjectedCollectionContainsAll(destination: MutableCollection<in T>, values: Collection<T>) =
    destination.containsAll(values)
fun <T> directProjectedCollectionAddAll(destination: MutableCollection<in T>, values: Collection<T>) =
    destination.addAll(values)
fun <T> directProjectedCollectionRemoveAll(destination: MutableCollection<in T>, values: Collection<T>) =
    destination.removeAll(values)
fun <T> directProjectedCollectionRetainAll(destination: MutableCollection<in T>, values: Collection<T>) =
    destination.retainAll(values)
fun <T> directProjectedListAddAllAt(destination: MutableList<in T>, values: Collection<T>) =
    destination.addAll(0, values)
fun <T> directProjectedListSet(destination: MutableList<in T>, value: T) = destination.set(0, value)
fun <T> directProjectedListRemoveAt(destination: MutableList<in T>) = destination.removeAt(0)
fun <T> directProjectedListIndexOf(destination: MutableList<in T>, value: T) = destination.indexOf(value)
fun <T> directProjectedListLastIndexOf(destination: MutableList<in T>, value: T) = destination.lastIndexOf(value)
fun <T> directProjectedListIterator(destination: MutableList<in T>) = destination.listIterator()
fun <T> directProjectedListSubList(destination: MutableList<in T>) = destination.subList(0, 2)
fun directStarListFirst(values: List<*>): Any? = values.iterator().next()
fun directStarSubListFirst(values: List<*>): Any? = values.subList(0, 1)[0]
fun directStarMutableRemoveAt(values: MutableList<*>): Any? = values.removeAt(0)
fun projectedCollectionArgumentSize(values: Collection<Any?>): Int = values.size
fun projectedCollectionArgumentCrossing(values: Collection<*>): Int = projectedCollectionArgumentSize(values)
fun projectedNullableCollectionArgumentSize(values: Collection<Any?>?): Int = values?.size ?: -1
fun projectedNullableCollectionArgumentCrossing(values: Collection<*>): Int =
    projectedNullableCollectionArgumentSize(values)
fun projectedListArgumentSize(values: List<Any?>): Int = values.size
fun projectedListArgumentCrossing(values: List<*>): Int = projectedListArgumentSize(values)
fun projectedSetArgumentSize(values: Set<Any?>): Int = values.size
fun projectedSetArgumentCrossing(values: Set<*>): Int = projectedSetArgumentSize(values)
fun projectedIterableArgumentCount(values: Iterable<Any?>): Int = values.count()
fun projectedIterableArgumentCrossing(values: Iterable<*>): Int = projectedIterableArgumentCount(values)
fun projectedCollectionPlus(values: Collection<*>): List<Any?> = values + "tail"
fun projectedCollectionSafeContains(values: Collection<*>, element: Any?): Boolean = values.contains(element)
fun projectedListSafeIndexOf(values: List<*>, element: Any?): Int = values.indexOf(element)
fun projectedListSafeLastIndexOf(values: List<*>, element: Any?): Int = values.lastIndexOf(element)
fun projectedSmartCastIsEmpty(value: Any): Boolean = if (value is Collection<*>) value.isEmpty() else false
fun <T, C : MutableList<in T>?> useSiteNullableProjectedListInsert(destination: C, value: T): C {
    destination?.add(0, value)
    return destination
}
fun useSiteDirectProjectedListInsert(
    destination: MutableList<in String>,
    value: String,
): MutableList<in String> {
    destination.add(0, value)
    return destination
}
fun useSiteNestedProjectedListInsert(
    destinations: List<MutableList<in String>>,
    value: String,
): List<MutableList<in String>> {
    destinations[0].add(0, value)
    return destinations
}
fun useSiteProjectedListCallable(
    destination: MutableList<in String>,
    transform: (MutableList<in String>) -> MutableList<in String>,
): MutableList<in String> = transform(destination)
fun <K, V> copyProjectedMap(source: Map<out K, V>): LinkedHashMap<K, V> = LinkedHashMap(source)
private var projectedMapSourceReads = 0
private fun projectedMapSource(): Map<String, Int> {
    projectedMapSourceReads++
    return mapOf("once" to 3)
}
class UseSiteProjectedListWriter<T, C : MutableList<in T>>(private val destination: C) {
    fun insert(value: T): C {
        destination.add(0, value)
        return destination
    }
}

fun useSiteCallable(
    box: UseSiteInvariant<in String>,
    transform: (UseSiteInvariant<in String>) -> UseSiteInvariant<out String>,
): String = transform(box).read()

class GenericsTests {
    @TestAttribute
    fun classAndFunction() {
        val bi = Box(42)
        assertEquals(42, bi.get())              // 42
        assertEquals(42, bi.value)              // 42
        assertEquals("hello", Box("hello").get()) // hello
        assertEquals(7, identity(7))            // 7
        assertEquals("world", identity("world")) // world
        val p = Pair2(3, "three")
        assertEquals(3, p.firstOne())           // 3
        assertEquals("three", p.secondOne())    // three
    }

    @TestAttribute
    fun genericInterface() {
        val a: Container<Int> = IntBox(99)
        assertEquals(99, a.item())                      // 99
        assertEquals("IntBox holding an Int", a.describe()) // IntBox holding an Int
        val b: Container<String> = Named("tag")
        assertEquals("tag", b.item())                   // tag
        assertEquals("Named holding a String", b.describe()) // Named holding a String
    }

    @TestAttribute
    fun boundedTypeParam() {
        assertEquals(7, maxOf2(3, 7))                   // 7
        assertEquals("banana", maxOf2("apple", "banana")) // banana
        assertEquals(10, SortedPair(10, 4).larger())   // 10
    }

    @TestAttribute
    fun genericMethodOnGenericClass() {
        val h = Holder(42)
        assertEquals(42, h.get())              // 42
        assertEquals("42 & hi", h.pairWith("hi")) // 42 & hi
        assertEquals("42 & 99", h.pairWith(99)) // 42 & 99
        assertEquals("x", firstOf("x", 7))     // x
    }

    @TestAttribute
    fun genericIndexer() {
        val s = Slot(10, 20)
        assertEquals(10, s[0])   // 10
        assertEquals(20, s[1])   // 20
        s[1] = 99
        assertEquals(99, s[1])   // 99
        val t = Slot("x", "y")
        t[0] = "z"
        assertEquals("z", t[0])  // z
    }

    @TestAttribute
    fun variance() {
        assertEquals("hello", useProducer(HelloProducer()))       // hello
        assertEquals("consumed: world", useConsumer(AnyConsumer())) // consumed: world
        assertEquals("7:hello", projectedProducerValues(arrayOf(IntProducer(7), HelloProducer())))
        assertEquals("hello", exactCovariantProducerArray()[0].produce().toString())
        assertEquals("7", projectedProducerFirst(arrayOf(IntProducer(7))).produce().toString())
        assertEquals("7", projectedProducerViaHelper(arrayOf(IntProducer(7))))
        assertEquals("7", projectedProducerViaMemberHelper(arrayOf(IntProducer(7))))
        assertEquals("7", projectedProducerViaCallback(arrayOf(IntProducer(7))))
        assertEquals("5:hello", localProjectedProducers())
        assertEquals("8", initializedProjectedProducerArray()[0].produce().toString())
        assertEquals("class", covariantClassArray()[0].value.toString())
        // String -> the synthetic CharSequence representation needs a separate runtime adapter when the value is
        // consumed. This case fixes the array boundary here: allocating the array itself must not assume CLR
        // covariance that System.String/dotkt$CharSequence do not have.
        assertEquals(1, charSequenceProducerArray().size)
        assertEquals(1, unsafeVarianceProducerArray().size)
        assertEquals("invariant", invariantProjectedValue(arrayOf(InvariantValue("invariant"))))
        assertEquals("10", spreadProjectedProducerArray()[0].produce().toString())
        assertEquals("hello", spreadProjectedProducerArray()[1].produce().toString())
        assertEquals(
            "11:hello",
            projectedProducerValues(spreadProjectedProducerInputs(
                arrayOf(IntProducer(11)),
                arrayOf(HelloProducer()),
            )),
        )
        assertEquals("9", nullableProjectedProducer(arrayOf(PrivateIntProducer())))

        val input = UseSiteAnyBox("initial")
        useSiteInParameter(input)
        assertEquals("in", input.read())
        assertEquals("out", useSiteOutResult().read())
        assertEquals("hello", useSiteVariantParameter(HelloProducer()))
        assertEquals("consumed: variant", useSiteVariantResult().consume("variant"))
        assertEquals("nested", useSiteNested(listOf(UseSiteStringBox("nested"))))
        assertEquals("bound", useSiteConstraint(input).read())
        val wideList = mutableListOf<Any>("tail")
        assertEquals(wideList, useSiteProjectedListInsert(wideList, "head"))
        assertEquals("head", wideList[0])
        assertEquals(wideList, useSiteProjectedCollectionAdd(wideList, "tail-added"))
        assertEquals("tail-added", wideList[wideList.size - 1])
        assertEquals(wideList, useSiteProjectedCollectionAddCaptured(wideList, "captured-tail"))
        assertEquals("captured-tail", wideList[wideList.size - 1])
        val wideSet = mutableSetOf<Any>("same")
        assertEquals(false, useSiteProjectedCollectionAddChanged(wideSet, "same"))
        assertEquals(true, useSiteProjectedCollectionAddChanged(wideSet, "new"))
        assertEquals(true, useSiteProjectedCollectionAddChanged(wideSet, 42))
        assertEquals(true, wideSet.contains(42))
        val intSet = mutableSetOf<Int>(1)
        assertEquals(true, useSiteProjectedCollectionAddChanged(intSet, 2))
        assertEquals(true, intSet.contains(2))
        assertEquals(true, useSiteProjectedListCollectionAdd(wideList, "list-add"))
        assertEquals(true, UseSiteProjectedCollectionAppender<String, MutableCollection<Any>>(wideSet).add("typed"))
        val projectedOps = mutableListOf<Any>("a", "b", "a")
        assertEquals(false, useSiteProjectedCollectionIsEmpty<String, MutableList<Any>>(projectedOps))
        assertEquals(true, useSiteProjectedCollectionContains(projectedOps, "a"))
        assertEquals(true, useSiteProjectedCollectionContainsAll(projectedOps, listOf("a", "b")))
        assertEquals(true, useSiteProjectedCollectionAddAll(projectedOps, listOf("c", "d")))
        assertEquals(true, useSiteProjectedCollectionRemoveAll(projectedOps, listOf("a")))
        assertEquals(true, useSiteProjectedCollectionRetainAll(projectedOps, listOf("b", "c")))
        assertEquals(true, useSiteProjectedListAddAllAt(projectedOps, listOf("head")))
        assertEquals("head", useSiteProjectedListSet(projectedOps, "new-head"))
        assertEquals("new-head", useSiteProjectedListRemoveAt<String, MutableList<Any>>(projectedOps))
        assertEquals(0, useSiteProjectedListIndexOf(projectedOps, "b"))
        assertEquals(1, useSiteProjectedListLastIndexOf(projectedOps, "c"))
        assertEquals("b", useSiteProjectedListIterator<String, MutableList<Any>>(projectedOps).next())
        val projectedSub = useSiteProjectedListSubList<String, MutableList<Any>>(projectedOps)
        assertEquals("b", projectedSub[0])
        projectedSub[0] = "B"
        projectedSub.add("d")
        assertEquals("B", projectedOps[0])
        assertEquals("d", projectedOps[2])

        // Projected routing must not turn a Kotlin implementer's virtual members into the BCL-backed defaults.
        // Every assertion below observes an override-only side effect, including the two listIterator overloads.
        val projectedOverrides = CollectionKotlinSlotCountingList()
        projectedOverrides.add(10); projectedOverrides.add(20); projectedOverrides.add(10)
        assertEquals(false, useSiteProjectedCollectionIsEmpty<Int, CollectionKotlinSlotCountingList>(projectedOverrides))
        assertEquals(1, projectedOverrides.isEmptyCalls)
        assertEquals(true, useSiteProjectedCollectionContains(projectedOverrides, 20))
        assertEquals(1, projectedOverrides.containsCalls)
        assertEquals(true, useSiteProjectedCollectionContainsAll(projectedOverrides, listOf(10, 20)))
        assertEquals(1, projectedOverrides.containsAllCalls)
        assertEquals(0, useSiteProjectedListIndexOf(projectedOverrides, 10))
        assertEquals(1, projectedOverrides.indexOfCalls)
        assertEquals(2, useSiteProjectedListLastIndexOf(projectedOverrides, 10))
        assertEquals(1, projectedOverrides.lastIndexOfCalls)
        assertEquals(10, useSiteProjectedListIterator<Int, CollectionKotlinSlotCountingList>(projectedOverrides).next())
        assertEquals(1, projectedOverrides.listIteratorCalls)
        assertEquals(20, useSiteProjectedListIteratorAt<Int, CollectionKotlinSlotCountingList>(projectedOverrides, 1).next())
        assertEquals(1, projectedOverrides.listIteratorAtCalls)
        val overrideSub = useSiteProjectedListSubList<Int, CollectionKotlinSlotCountingList>(projectedOverrides)
        assertEquals(1, projectedOverrides.subListCalls)
        overrideSub[0] = 11
        assertEquals(11, projectedOverrides[0])

        // The receiver override is declared at Collection<Any>, while these arguments are Collection<Int>.
        // IReadOnlyCollection<Int32> is not CLR-variant to IReadOnlyCollection<Object>; the slot bridge must provide
        // the implementer's exact live view instead of emitting a cast that only happens to work for reference types.
        val valueArgumentOverrides = CollectionKotlinSlotCounting<Any>()
        valueArgumentOverrides.add(1); valueArgumentOverrides.add(2); valueArgumentOverrides.add(3)
        assertEquals(true, useSiteProjectedCollectionContainsAll<Int, CollectionKotlinSlotCounting<Any>>(
            valueArgumentOverrides, listOf(1, 2)))
        assertEquals(1, valueArgumentOverrides.containsAllCalls)
        assertEquals(true, useSiteProjectedCollectionAddAll<Int, CollectionKotlinSlotCounting<Any>>(
            valueArgumentOverrides, listOf(4)))
        assertEquals(1, valueArgumentOverrides.addAllCalls)
        assertEquals(true, useSiteProjectedCollectionRemoveAll<Int, CollectionKotlinSlotCounting<Any>>(
            valueArgumentOverrides, listOf(2)))
        assertEquals(1, valueArgumentOverrides.removeAllCalls)
        assertEquals(true, useSiteProjectedCollectionRetainAll<Int, CollectionKotlinSlotCounting<Any>>(
            valueArgumentOverrides, listOf(1, 4)))
        assertEquals(1, valueArgumentOverrides.retainAllCalls)
        assertEquals("[1, 4]", valueArgumentOverrides.snapshot().toString())

        val directOps = mutableListOf<Any>("a", "b", "a")
        assertEquals(false, directProjectedCollectionIsEmpty<String>(directOps))
        assertEquals(true, directProjectedCollectionContains(directOps, "a"))
        assertEquals(true, directProjectedCollectionContainsAll(directOps, listOf("a", "b")))
        assertEquals(true, directProjectedCollectionAddAll(directOps, listOf("c", "d")))
        assertEquals(true, directProjectedCollectionRemoveAll(directOps, listOf("a")))
        assertEquals(true, directProjectedCollectionRetainAll(directOps, listOf("b", "c")))
        assertEquals(true, directProjectedListAddAllAt(directOps, listOf("head")))
        assertEquals("head", directProjectedListSet(directOps, "new-head"))
        assertEquals("new-head", directProjectedListRemoveAt<String>(directOps))
        assertEquals(0, directProjectedListIndexOf(directOps, "b"))
        assertEquals(1, directProjectedListLastIndexOf(directOps, "c"))
        assertEquals("b", directProjectedListIterator<String>(directOps).next())
        val directSub = directProjectedListSubList<String>(directOps)
        directSub[0] = "direct-B"
        assertEquals("direct-B", directOps[0])

        val projectedValueOps = mutableListOf(1, 2, 1)
        assertEquals(false, useSiteProjectedCollectionIsEmpty<Int, MutableList<Int>>(projectedValueOps))
        assertEquals(true, useSiteProjectedCollectionContains(projectedValueOps, 2))
        assertEquals(true, useSiteProjectedCollectionContainsAll(projectedValueOps, listOf(1, 2)))
        assertEquals(true, useSiteProjectedCollectionRemoveAll(projectedValueOps, listOf(1)))
        assertEquals(true, useSiteProjectedCollectionAddAll(projectedValueOps, listOf(3, 4)))
        assertEquals(true, useSiteProjectedCollectionRetainAll(projectedValueOps, listOf(2, 3)))
        assertEquals(true, useSiteProjectedListAddAllAt(projectedValueOps, listOf(0)))
        assertEquals(0, useSiteProjectedListSet(projectedValueOps, 5))
        assertEquals(5, useSiteProjectedListRemoveAt<Int, MutableList<Int>>(projectedValueOps))
        assertEquals(0, useSiteProjectedListIndexOf(projectedValueOps, 2))
        assertEquals(1, useSiteProjectedListLastIndexOf(projectedValueOps, 3))
        assertEquals(2, useSiteProjectedListIterator<Int, MutableList<Int>>(projectedValueOps).next())

        val starList: List<*> = listOf(7, "seven")
        assertEquals(7, directStarListFirst(starList))
        assertEquals(7, directStarSubListFirst(starList))
        val starMutable: MutableList<*> = mutableListOf(8, "eight")
        assertEquals(8, directStarMutableRemoveAt(starMutable))
        val projectedInts: Collection<*> = listOf(1, 2)
        assertEquals(2, projectedCollectionArgumentCrossing(projectedInts))
        assertEquals(2, projectedNullableCollectionArgumentCrossing(projectedInts))
        assertEquals(2, projectedListArgumentCrossing(listOf(1, 2)))
        assertEquals(2, projectedSetArgumentCrossing(setOf(1, 2)))
        assertEquals(2, projectedIterableArgumentCrossing(listOf(1, 2)))
        val projectedPlus = projectedCollectionPlus(projectedInts)
        assertEquals(3, projectedPlus.size)
        assertEquals(1, projectedPlus[0])
        assertEquals(2, projectedPlus[1])
        assertEquals("tail", projectedPlus[2])
        assertEquals(false, projectedCollectionSafeContains(projectedOverrides, "not-an-int"))
        assertEquals(-1, projectedListSafeIndexOf(projectedOverrides, "not-an-int"))
        assertEquals(-1, projectedListSafeLastIndexOf(projectedOverrides, null))
        assertEquals(1, projectedOverrides.containsCalls)
        assertEquals(1, projectedOverrides.indexOfCalls)
        assertEquals(1, projectedOverrides.lastIndexOfCalls)
        assertEquals(false, projectedCollectionSafeContains(CollectionKotlinSlotCounting<Int>(), "not-an-int"))
        assertEquals(false, projectedSmartCastIsEmpty(projectedOverrides))
        assertEquals(2, projectedOverrides.isEmptyCalls)
        assertEquals(true, useSiteDirectProjectedCollectionAdd(wideSet, "direct"))
        assertEquals(true, useSiteNullableProjectedCollectionAdd(wideSet, "nullable"))
        assertEquals(false, useSiteNullableProjectedCollectionAdd(null, "missing"))
        assertEquals(wideList, useSiteDirectProjectedListInsert(wideList, "direct-head"))
        assertEquals("direct-head", wideList[0])
        val nestedWideLists = listOf(wideList)
        assertEquals(nestedWideLists, useSiteNestedProjectedListInsert(nestedWideLists, "nested-head"))
        assertEquals("nested-head", wideList[0])
        assertEquals(
            wideList,
            useSiteProjectedListCallable(wideList) { destination ->
                destination.add(0, "callable-head")
                destination
            },
        )
        assertEquals("callable-head", wideList[0])
        assertEquals(mapOf("key" to 1), copyProjectedMap(mapOf("key" to 1)))
        val narrowMap: Map<String, Int> = mapOf("wide-key" to 2)
        assertEquals(mapOf<Any, Int>("wide-key" to 2), copyProjectedMap<Any, Int>(narrowMap))
        projectedMapSourceReads = 0
        assertEquals(mapOf<Any, Int>("once" to 3), LinkedHashMap<Any, Int>(projectedMapSource()))
        assertEquals(1, projectedMapSourceReads)
        val nullableWideList: MutableList<Any>? = mutableListOf("tail")
        assertEquals(nullableWideList, useSiteNullableProjectedListInsert(nullableWideList, "nullable-head"))
        assertEquals("nullable-head", nullableWideList!![0])
        assertEquals(wideList, UseSiteProjectedListWriter<String, MutableList<Any>>(wideList).insert("class-head"))
        assertEquals("class-head", wideList[0])
        assertEquals("number", projectedComparableText(NumberComparable("number")))
        assertEquals("lambda", useSiteCallable(input) { UseSiteStringBox("lambda") })
        val constructed = constructFromUseSiteProjection(UseSiteStringBox("captured"))
        assertEquals("captured", constructed.box.read())
        assertEquals("single", constructed.selected)
        assertEquals("nullable:7", UseSiteProjectedConstructor.fromNullable(UseSiteStringBox("nullable")).selected)
        assertEquals("star:1", UseSiteProjectedConstructor.fromStar(UseSiteStringBox("star")).selected)
        assertEquals("array:1", UseSiteProjectedConstructor.fromArray(UseSiteStringBox("array")).selected)
        val projectedBoundDestination = mutableListOf<Any>("tail")
        val projectedBoundWriter =
            UseSiteProjectedBoundOuter<String>().Writer<MutableList<Any>>(projectedBoundDestination)
        assertEquals(projectedBoundDestination, projectedBoundWriter.insert("inner"))
        assertEquals("inner", projectedBoundDestination[0])
    }

    @TestAttribute
    fun typeParameterReceiverDispatch() {
        val v = TaggedKeyed(4)
        assertEquals(4, tagOfParam(v))
        assertEquals(4, tagOfLocal(v))
        assertEquals(40, keyOfParam(v))
        assertEquals(4, tagOfNotNull(v))
        val h = TaggedHolder(v)
        assertEquals(4, h.tagOfField())
        assertEquals(4, h.tagOfCallResult())
        assertEquals(4, h.accessorTag)
        h.accessorTag = 11                                 // the SETTER body, a separate walk from the getter
        assertEquals(11, h.accessorTag)
        assertEquals(110, keyOfParam(v))
    }

    @TestAttribute
    fun typeParameterReceiverKeepsMemberSelection() {
        val d = DescribedTag(9)
        assertEquals("int:7/str:s", describeOverloads(d))   // NOT "str:7/..." — the Int overload
        assertEquals("a", genericMemberOnTypeParam(d))
        assertEquals("int:4", DescribedHolder(d).accessorDescription)
        assertEquals(60, rootThroughBase(IntLeaf()))        // 55 (declared on the generic BASE) + 5
        assertEquals(126, classBoundReceiver(TagDerived())) // 66 (virtual) + 60 (non-virtual)
    }
}
