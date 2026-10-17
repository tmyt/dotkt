@file:Suppress("DEPRECATION_ERROR")

package roundtrip.numbertests

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.numbers.*

class ConsumerNumber : LibraryNumber(1) { override fun toInt(): Int = 83 }
open class ConsumerMiddle : LibraryNumber(1)
class ConsumerLeaf : ConsumerMiddle() { override fun toInt(): Int = 83 }
class ConsumerCharacter : LibraryNumber(65) {
    var calls: Int = 0
    @kotlin.clr.ClrName("CustomCharacter")
    override fun toChar(): Char {
        calls++
        return if (calls == 1) super.toChar() else '?'
    }
}

class NumberRoundtripTests {
    @TestAttribute
    fun sourceSlotsBoundsAndOverridesSurviveDllConsumption() {
        assertEquals(17, numberValue(17))
        assertEquals("number", numberChoice(17))
        assertEquals("any", numberChoice("17"))
        assertEquals(18, boundedNumber(18))
        assertEquals(18, numberCast<Number>(18L).toInt())
        val uncheckedNull: Any? = numberCast<Number>(null)
        check(uncheckedNull == null)
        var rejected = false
        try { numberCast<Number>("bad") } catch (_: ClassCastException) { rejected = true }
        check(rejected)
        assertEquals(19, NumberBound(19).read())
        val slot = NumberSlots(20, null)
        val sourceNumber: Number = slot.read()
        assertEquals(20, sourceNumber.toInt())
        slot.value = 21L
        assertEquals(21, slot.value.toInt())
        check(slot.nullable == null)
        val custom: Number = ConsumerNumber()
        assertEquals(83, numberValue(custom))
        val leaf: Number = ConsumerLeaf()
        assertEquals(83, leaf.toInt())
        assertEquals(1L, leaf.toLong())
        assertEquals(83.toChar(), leaf.toChar())
        assertEquals(0, (leaf as Comparable<Number>).compareTo(83))
        check(isNumberType<Comparable<Number>>(leaf))
        check(!isNumberType<Comparable<Number>>(Any()))
        val character = ConsumerCharacter()
        assertEquals('A', (character as Number).toChar())
        assertEquals(1, character.calls)
        assertEquals('B', (LibraryNumber(66) as Number).toChar())
        val order: Comparable<Number> = NumberOrder()
        assertEquals(22, order.compareTo(22))
        val array = arrayOf<Number>(23, custom)
        check(numberArray(array) === array)
        assertEquals(83, numberArray(array)[1].toInt())
        check(isNumberType<Number>(24))
        check(!isNumberType<Number>('a'))
        check(isNumberType<Array<Number>>(array))
        check(!isNumberType<Array<Number>>(arrayOf("precise")))
        val projected: Any = arrayOf<Array<out Number>>(arrayOf(1))
        check(isNumberType<Array<Array<out Number>>>(projected))
    }
}
