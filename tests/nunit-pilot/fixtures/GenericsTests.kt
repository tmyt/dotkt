// Generics battery — migrates cases/il-generic .. il-generic6 (the G-1..G-6 progressive milestones the
// audit §5 condemns as 6 permanent processes). Here they are 6 @TestAttribute methods in ONE fixture,
// compiled once into the shared assembly. Same plain-Kotlin, same oracle, same compile conditions.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

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

// G-3: bounded type parameters <T : Comparable<T>> -> CLR generic constraint (IComparable<T>).
fun <T : Comparable<T>> maxOf2(a: T, b: T): T = if (a > b) a else b
class SortedPair<T : Comparable<T>>(val a: T, val b: T) { fun larger(): T = if (a > b) a else b }

// G-4: generic method on a generic class + generic method returning its type param.
class Holder<T>(val value: T) {
    fun <R> pairWith(other: R): String = "$value & $other"
    fun get(): T = value
}
fun <A, B> firstOf(a: A, b: B): A = a

// G-5: generic indexer (operator get/set on a generic class).
class Slot<T>(var a: T, var b: T) {
    operator fun get(i: Int): T = if (i == 0) a else b
    operator fun set(i: Int, v: T) { if (i == 0) a = v else b = v }
}

// G-6: declaration-site variance over reference types -> CLR covariant/contravariant interfaces.
interface Producer<out T> { fun produce(): T }
interface Consumer<in T> { fun consume(t: T): String }
class HelloProducer : Producer<String> { override fun produce(): String = "hello" }
class AnyConsumer : Consumer<Any> { override fun consume(t: Any): String = "consumed: $t" }
fun useProducer(p: Producer<Any>): String = p.produce().toString()
fun useConsumer(c: Consumer<String>): String = c.consume("world")

class GenericsTests {
    @TestAttribute
    fun g1_classAndFunction() {
        val bi = Box(42)
        ClassicAssert.AreEqual(42, bi.get())
        ClassicAssert.AreEqual(42, bi.value)
        ClassicAssert.AreEqual("hello", Box("hello").get())
        ClassicAssert.AreEqual(7, identity(7))
        ClassicAssert.AreEqual("world", identity("world"))
        val p = Pair2(3, "three")
        ClassicAssert.AreEqual(3, p.firstOne())
        ClassicAssert.AreEqual("three", p.secondOne())
    }

    @TestAttribute
    fun g2_genericInterface() {
        val a: Container<Int> = IntBox(99)
        ClassicAssert.AreEqual(99, a.item())
        ClassicAssert.AreEqual("IntBox holding an Int", a.describe())
        val b: Container<String> = Named("tag")
        ClassicAssert.AreEqual("tag", b.item())
        ClassicAssert.AreEqual("Named holding a String", b.describe())
    }

    @TestAttribute
    fun g3_boundedTypeParam() {
        ClassicAssert.AreEqual(7, maxOf2(3, 7))
        ClassicAssert.AreEqual("banana", maxOf2("apple", "banana"))
        ClassicAssert.AreEqual(10, SortedPair(10, 4).larger())
    }

    @TestAttribute
    fun g4_genericMethodOnGenericClass() {
        val h = Holder(42)
        ClassicAssert.AreEqual(42, h.get())
        ClassicAssert.AreEqual("42 & hi", h.pairWith("hi"))
        ClassicAssert.AreEqual("42 & 99", h.pairWith(99))
        ClassicAssert.AreEqual("x", firstOf("x", 7))
    }

    @TestAttribute
    fun g5_genericIndexer() {
        val s = Slot(10, 20)
        ClassicAssert.AreEqual(10, s[0])
        ClassicAssert.AreEqual(20, s[1])
        s[1] = 99
        ClassicAssert.AreEqual(99, s[1])
        val t = Slot("x", "y")
        t[0] = "z"
        ClassicAssert.AreEqual("z", t[0])
    }

    @TestAttribute
    fun g6_variance() {
        ClassicAssert.AreEqual("hello", useProducer(HelloProducer()))
        ClassicAssert.AreEqual("consumed: world", useConsumer(AnyConsumer()))
    }
}
