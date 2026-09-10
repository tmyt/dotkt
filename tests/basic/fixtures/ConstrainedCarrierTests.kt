import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.clr.ClrRef
import kotlin.clr.byref

open class OwnerBoundSink<in T> {
    open fun <R : T> render(value: R): String = "base:$value"
    fun <R : T> echo(value: R): R = value
    fun <R : T> withCallback(value: R, callback: (R) -> String): String = callback(value)
    fun <R : S, S : T> transitive(value: R): String = value.toString()
    fun <R : T> update(value: R, first: ClrRef<Int>, second: ClrRef<Int>): String {
        first.value = 12
        second.value = second.value + 3
        return value.toString()
    }
    fun <R : T> fail(value: R): String = throw IllegalStateException(value.toString())
    fun <R : T> record(key: T, value: R, log: (String) -> Unit) { log("$key:$value") }
}

class OwnerBoundDerived : OwnerBoundSink<Any>() {
    override fun <R : Any> render(value: R): String = "derived:$value"
}

class OwnerBoundGenericDerived<in T> : OwnerBoundSink<T>() {
    override fun <R : T> render(value: R): String = "generic:$value"
}

open class OwnerBoundAnimal
class OwnerBoundDog : OwnerBoundAnimal() {
    override fun toString(): String = "dog"
}

class OwnerBoundAnimalDerived : OwnerBoundSink<OwnerBoundAnimal>() {
    override fun <R : OwnerBoundAnimal> render(value: R): String = "animal:$value"
}

class OwnerBoundWide<in T> {
    fun <R : T> accept(
        value: R, a: Int, b: Int, c: Int, d: Int, e: Int, f: Int,
        g: Int, h: Int, i: Int, j: Int, k: Int, l: Int,
        m: Int, n: Int, o: Int, p: Int, q: Int, s: Int,
        u: Int, v: Int, w: Int, x: Int,
    ): R = value
}

interface OwnerBoundContract<in T> {
    fun <R : T> invoke(value: R): String
}

class OwnerBoundContractImpl : OwnerBoundContract<Any> {
    override fun <R : Any> invoke(value: R): String = "interface:$value"
}

class OwnerComparableSink<out T> {
    fun <R : Comparable<T>> render(value: R): String = value.toString()
}

class OwnerComparableNumbers : Comparable<Number> {
    override fun compareTo(other: Number): Int = other.toInt()
    override fun toString(): String = "numbers"
}

class OwnerForeignComparer : System.Collections.Generic.IComparer<Any> {
    override fun Compare(x: Any?, y: Any?): Int = x.toString().toInt()
}

class OwnerComparableValue<out T>(val item: T) {
    fun <R : System.Collections.Generic.IComparer<T>> compare(value: R): Int = value.Compare(item, item)
    fun <R : System.Collections.Generic.IComparer<T>> deferred(value: R): () -> Int =
        { value.Compare(item, item) }
}

interface OwnerLocalComparable<in T> {
    fun compare(value: T): Int
}

class OwnerLocalValues : OwnerLocalComparable<Any> {
    override fun compare(value: Any): Int = value.toString().toInt()
}

class OwnerLocalValue<out T>(val item: T) {
    fun <R : OwnerLocalComparable<T>> compare(value: R): Int = value.compare(item)
    fun <R : OwnerLocalComparable<T>> deferred(value: R): () -> Int = { value.compare(item) }
}

interface OwnerBoundTag
class OwnerBoundTagged : OwnerBoundTag
fun <X : OwnerBoundTag> retainOwnerBoundTag(value: X): X = value
class OwnerBoundTaggedSink<in T : OwnerBoundTag> {
    fun <R : T> retain(value: R): R = retainOwnerBoundTag(value)
}

class OwnerBoundBox<T>(private var field: T) {
    fun <R : T> pass(value: R): T = value
    fun <R : T> store(value: R) { field = value }
    fun <R : T> take(value: R): T = sink(value)
    private fun sink(value: T): T = value
    fun get(): T = field
}

class ConstrainedCarrierTests {
    @TestAttribute
    fun ownerBoundsKeepIndependentProofsAndValueConversions() {
        val tagged = OwnerBoundTagged()
        assertEquals(tagged, OwnerBoundTaggedSink<OwnerBoundTag>().retain(tagged))
        val boxed = OwnerBoundBox<Any>("before")
        assertEquals("text", boxed.pass("text"))
        assertEquals(42, boxed.take(42))
        boxed.store(43)
        assertEquals(43, boxed.get())
        val integers = OwnerBoundBox(1)
        assertEquals(2, integers.pass(2))
        integers.store(3)
        assertEquals(3, integers.get())
        assertEquals(4, integers.take(4))
    }

    @TestAttribute
    fun ownerConstrainedNamedBoundAllowsValueTypeVariance() {
        val widened: OwnerComparableSink<Number> = OwnerComparableSink<Int>()
        assertEquals("numbers", widened.render(OwnerComparableNumbers()))
        val comparable: OwnerComparableValue<Any> = OwnerComparableValue(17)
        assertEquals(17, comparable.compare(OwnerForeignComparer()))
        assertEquals(17, comparable.deferred(OwnerForeignComparer())())
        val local: OwnerLocalValue<Any> = OwnerLocalValue(23)
        assertEquals(23, local.compare(OwnerLocalValues()))
        assertEquals(23, local.deferred(OwnerLocalValues())())
    }

    @TestAttribute
    fun ownerConstrainedGenericCallsKeepActualMethodArguments() {
        val strings: OwnerBoundSink<String> = OwnerBoundSink<Any>()
        assertEquals("text", strings.echo("text"))
        assertEquals("callback:text", strings.withCallback("text") { "callback:$it" })
        assertEquals("transitive", strings.transitive<String, String>("transitive"))
        val integers: OwnerBoundSink<Int> = OwnerBoundSink<Any>()
        assertEquals(42, integers.echo(42))
        assertEquals("43", integers.withCallback(42) { (it + 1).toString() })
        val derived: OwnerBoundSink<String> = OwnerBoundDerived()
        assertEquals("derived:dispatch", derived.render("dispatch"))
        val contract: OwnerBoundContract<String> = OwnerBoundContractImpl()
        assertEquals("interface:dispatch", contract.invoke("dispatch"))
        val comparable = OwnerComparableSink<String>()
        assertEquals("comparable", comparable.render("comparable"))
        var shared = 0
        assertEquals("alias", strings.update("alias", byref(shared), byref(shared)))
        assertEquals(15, shared)
        var caught = ""
        try {
            strings.fail("direct-exception")
        } catch (failure: IllegalStateException) {
            caught = failure.message ?: ""
        }
        assertEquals("direct-exception", caught)
        var recorded = ""
        strings.record("key", "value") { recorded = it }
        assertEquals("key:value", recorded)
        val generic: OwnerBoundSink<String> = OwnerBoundGenericDerived<Any>()
        assertEquals("generic:text", generic.render("text"))
        val animals: OwnerBoundSink<OwnerBoundDog> = OwnerBoundAnimalDerived()
        assertEquals("animal:dog", animals.render(OwnerBoundDog()))
        val wide: OwnerBoundWide<String> = OwnerBoundWide<Any>()
        assertEquals("wide", wide.accept("wide", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
            12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22))
    }
}
