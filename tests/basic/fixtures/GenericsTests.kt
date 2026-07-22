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
}
