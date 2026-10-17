package roundtrip.numbertests

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.numbers.*

class ConsumerNumber : LibraryNumber(1) { override fun toInt(): Int = 83 }

class NumberRoundtripTests {
    @TestAttribute
    fun sourceSlotsBoundsAndOverridesSurviveDllConsumption() {
        assertEquals(17, numberValue(17))
        assertEquals("number", numberChoice(17))
        assertEquals("any", numberChoice("17"))
        assertEquals(18, boundedNumber(18))
        assertEquals(19, NumberBound(19).read())
        val slot = NumberSlots(20, null)
        val sourceNumber: Number = slot.read()
        assertEquals(20, sourceNumber.toInt())
        slot.value = 21L
        assertEquals(21, slot.value.toInt())
        check(slot.nullable == null)
        val custom: Number = ConsumerNumber()
        assertEquals(83, numberValue(custom))
        val order: Comparable<Number> = NumberOrder()
        assertEquals(22, order.compareTo(22))
        val array = arrayOf<Number>(23, custom)
        check(numberArray(array) === array)
        assertEquals(83, numberArray(array)[1].toInt())
        check(isNumberType<Number>(24))
        check(!isNumberType<Number>('a'))
        check(isNumberType<Array<Number>>(array))
        check(!isNumberType<Array<Number>>(arrayOf("precise")))
    }
}
