@file:Suppress("UNCHECKED_CAST", "DEPRECATION_ERROR")

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine
import kotlin.coroutines.suspendCoroutine

abstract class NumericRoot : Number()
open class AuthoredNumber(private val value: Int) : NumericRoot() {
    override fun toByte(): Byte = value.toByte()
    override fun toShort(): Short = value.toShort()
    @kotlin.clr.ClrName("NumericInt")
    override fun toInt(): Int = value
    override fun toLong(): Long = value.toLong()
    override fun toFloat(): Float = value.toFloat()
    override fun toDouble(): Double = value.toDouble()
}

class OverridingNumber : AuthoredNumber(1) { override fun toInt(): Int = 73 }
class BaseCallNumber : AuthoredNumber(65) {
    var calls: Int = 0
    override fun toChar(): Char {
        calls++
        return if (calls == 1) super.toChar() else '?'
    }
}
class NumericOrder : Comparable<Number> { override fun compareTo(other: Number): Int = other.toInt() }
class NumberSlot(var number: Number, val optional: Number?)
class NumberBox<T : Number>(var value: T) { fun intValue(): Int = value.toInt() }
interface NumberProducer { fun value(): Number }
class IntNumberProducer : NumberProducer { override fun value(): Int = 31 }
open class NumberSource { open fun value(): Number = 1 }
class IntNumberSource : NumberSource() { override fun value(): Int = 32 }
private fun numberValue(value: Number): Int = value.toInt()
private fun numberChoice(value: Number): String = "number"
private fun numberChoice(value: Any): String = "any"
private fun <T : Number> boundedNumber(value: T): Int = value.toInt()
private fun <T : Number> boundedNumberCast(value: Any?): T = value as T
private fun <T : Number> boundedNumberSafe(value: Any?): T? = value as? T
private class NumberCaster<A, N : Number> { fun <T : N> cast(value: Any?): T = value as T }
private fun <T> numberArrayIdentity(value: Array<T>): Array<T> = value
private fun <T : Number> boundedNumberArray(value: Any, replacement: T): Boolean {
    val array = value as? Array<T> ?: return false
    check(array[0].toInt() == 1)
    array[0] = replacement
    return array[0].toInt() == replacement.toInt()
}
private inline fun <reified T> numberIs(value: Any?): Boolean = value is T
private inline fun <reified T> numberSafe(value: Any?): T? = value as? T
private inline fun <reified T> numberCast(value: Any?): T = value as T
private inline fun <reified T> numberLifted(value: Any?): () -> Boolean = { numberIs<T>(value) }
private suspend fun suspendedNumber(value: Number): Number = value

class NumberTests {
    @TestAttribute
    fun primitiveValuesConversionsAndMembership() {
        val values = arrayOf<Number>(17.toByte(), 17.toShort(), 17, 17L, 17.75f, 17.75)
        for (value in values) {
            check((value as Any) is Number)
            check(numberIs<Number>(value))
            assertEquals(17, numberValue(value))
            assertEquals(17.toByte(), value.toByte())
            assertEquals(17.toShort(), value.toShort())
            assertEquals(17L, value.toLong())
        }
        assertEquals(17.75, values[5].toDouble())
        assertEquals(17.75f, values[4].toFloat())
        assertEquals((-257).toByte(), (AuthoredNumber(-257) as Number).toByte())
        assertEquals((-257L).toShort(), (-257L as Number).toShort())
        assertEquals(-12, (-12.75 as Number).toInt())
        for (other in arrayOf<Any>('a', true, "17", 17u, 17uL, 17.toUByte(), 17.toUShort(), Any())) {
            check(other !is Number)
            check(!numberIs<Number>(other))
            check(numberSafe<Number>(other) == null)
        }
        check(!numberIs<Number>(null))
        check(numberIs<Number?>(null))
        val absent: Any? = null
        check(absent is Number?)
        check(absent !is Number)
        check((17 as Any) is Number?)
        check(("bad" as Any) !is Number?)
        check(numberLifted<Number>(17)())
        check(!numberLifted<Number>('a')())
        var rejected = false
        try { val ignored = "bad" as Any as Number } catch (_: ClassCastException) { rejected = true }
        check(rejected)
        rejected = false
        try { boundedNumberCast<Number>("bad") } catch (_: ClassCastException) { rejected = true }
        check(rejected)
        rejected = false
        try { NumberCaster<Any, Number>().cast<Number>("bad") } catch (_: ClassCastException) { rejected = true }
        check(rejected)
        check(boundedNumberSafe<Number>("bad") == null)
        assertEquals(17, boundedNumberCast<Number>(17).toInt())
        val uncheckedNull: Any? = boundedNumberCast<Number>(null)
        val inheritedBoundNull: Any? = NumberCaster<Any, Number>().cast<Number>(null)
        check(uncheckedNull == null && inheritedBoundNull == null)
        check(boundedNumberSafe<Number>(null) == null)
        rejected = false
        try { numberCast<Number>(null) } catch (_: ClassCastException) { rejected = true }
        check(rejected)
    }

    @TestAttribute
    fun storageBoundsVirtualDispatchAndLiftedValues() {
        assertEquals(17, numberValue(17))
        assertEquals("number", numberChoice(17))
        assertEquals("any", numberChoice("17"))
        assertEquals(7, (1..7).step(2).last)
        assertEquals(18, boundedNumber(18))
        assertEquals(19, NumberBox(19).intValue())
        assertEquals(19, NumberBox<Number>(19.75).intValue())
        val producer: NumberProducer = IntNumberProducer()
        val source: NumberSource = IntNumberSource()
        assertEquals(31, producer.value().toInt())
        assertEquals(32, source.value().toInt())
        val custom: Number = OverridingNumber()
        assertEquals(73, numberValue(custom))
        assertEquals(73, boundedNumber(custom))
        check(numberIs<Number>(custom))
        check(numberSafe<Number>(custom) === custom)
        val baseCall = BaseCallNumber()
        assertEquals('A', (baseCall as Number).toChar())
        assertEquals(1, baseCall.calls)
        val slot = NumberSlot(20, null)
        slot.number = 21L
        assertEquals(21, slot.number.toInt())
        check(slot.optional == null)
        val boxed: Any = slot.number
        val retained: Number = boxed as Number
        check((retained as Any) === boxed)
        val captured = { slot.number.toInt() }
        assertEquals(21, captured())
        val reference: (Number) -> Int = ::numberValue
        assertEquals(22, reference(22))
        val numbers: List<Number> = listOf(23, custom)
        assertEquals(23, numbers[0].toInt())
        assertEquals(73, numbers[1].toInt())
        val order: Comparable<Number> = NumericOrder()
        assertEquals(24, order.compareTo(24))
        assertEquals(25, (NumericOrder() as System.IComparable).CompareTo(25))
        var resumed: Number? = null
        var pending: Continuation<Number>? = null
        val block: suspend () -> Number = { suspendedNumber(suspendCoroutine { pending = it }) }
        block.startCoroutine(object : Continuation<Number> {
            override val context = EmptyCoroutineContext
            override fun resumeWith(result: Result<Number>) { resumed = result.getOrThrow() }
        })
        check(resumed == null)
        pending!!.resumeWith(Result.success(26L))
        assertEquals(26, resumed!!.toInt())
    }

    @TestAttribute
    fun arraysUseTheExistingComponentStorageContract() {
        val array = arrayOf<Number>(1, 2L)
        array[0] = 3.75
        val forwarded = numberArrayIdentity(array)
        check(forwarded === array)
        assertEquals(3, forwarded[0].toInt())
        check(numberIs<Array<Number>>(array))
        check(numberIs<Array<Number>>(arrayOf<Any>("erased")))
        check(!numberIs<Array<Number>>(arrayOf("precise")))
        val precise: Any = arrayOf("precise")
        check((precise as? Array<Number>) == null)
        check(numberSafe<Array<Number>>(precise) == null)
        val projected: Array<out Number> = arrayOf(4)
        assertEquals(4, projected[0].toInt())
        check(numberIs<Array<out Number>>(projected))
        check(!numberIs<Array<out Number>>(precise))
        val nativeValues: Any = arrayOf(1)
        check(!numberIs<Array<Number>>(nativeValues))
        check(numberSafe<Array<Number>>(nativeValues) == null)
        check((nativeValues as? Array<Number>) == null)
        var rejected = false
        try { val ignored = nativeValues as Array<Number> } catch (_: ClassCastException) { rejected = true }
        check(rejected)
        rejected = false
        try { val ignored = numberCast<Array<Number>>(nativeValues) } catch (_: ClassCastException) { rejected = true }
        check(rejected)
        val checked = (array as Any) as Array<Number>
        assertEquals(2, checked[1].toInt())
        val reified = numberCast<Array<Number>>(array)
        reified[1] = 5L
        assertEquals(5, reified[1].toInt())
        check(boundedNumberArray<Number>(arrayOf(1), 6))
        check(boundedNumberArray(arrayOf(1), 7))
        check(!boundedNumberArray<Number>(arrayOf("precise"), 8))
        val nestedNative: Any = arrayOf(arrayOf(1))
        check(!numberIs<Array<Array<Number>>>(nestedNative))
        check(numberSafe<Array<Array<Number>>>(nestedNative) == null)
        check((nestedNative as? Array<Array<Number>>) == null)
        val nested: Any = arrayOf(arrayOf<Number>(1))
        val nestedChecked = numberCast<Array<Array<Number>>>(nested)
        nestedChecked[0][0] = 9L
        assertEquals(9, nestedChecked[0][0].toInt())
        val nestedProjected: Any = arrayOf<Array<out Number>>(arrayOf(1))
        check(numberIs<Array<Array<out Number>>>(nestedProjected))
        assertEquals(1, numberSafe<Array<Array<out Number>>>(nestedProjected)!![0][0].toInt())
        assertEquals(1, numberCast<Array<Array<out Number>>>(nestedProjected)[0][0].toInt())
        assertEquals(1, (nestedProjected as Array<Array<out Number>>)[0][0].toInt())
        assertEquals(1, (nestedProjected as? Array<Array<out Number>>)!![0][0].toInt())
        val projectedView = numberCast<Array<Array<out Number>>>(nestedProjected)
        projectedView[0] = arrayOf(2L)
        assertEquals(2, projectedView[0][0].toInt())
        check(numberIs<Array<Array<out Number>>>(nestedNative))
        check(!numberIs<Array<Array<out Number>>>(arrayOf(arrayOf("precise"))))
    }
}
