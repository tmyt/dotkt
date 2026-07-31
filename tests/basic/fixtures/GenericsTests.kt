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
// NUnit's static asserts live on ClassicAssert.Companion in DotKt (C# static classes surface their statics
// on the Kotlin `.Companion`); `import ... as` aliases the member as a callable so tests read idiomatically.
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

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
class HelloProducer : Producer<String> { override fun produce(): String = "hello" }
class AnyConsumer : Consumer<Any> { override fun consume(t: Any): String = "consumed: $t" }
fun useProducer(p: Producer<Any>): String = p.produce().toString()   // covariance: Producer<String> flows in
fun useConsumer(c: Consumer<String>): String = c.consume("world")    // contravariance: Consumer<Any> flows in

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
