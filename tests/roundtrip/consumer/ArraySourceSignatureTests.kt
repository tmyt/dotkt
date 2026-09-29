@file:OptIn(kotlin.ExperimentalUnsignedTypes::class)

package roundtriptests.arraysource

import NUnit.Framework.TestAttribute
import roundtrip.arraysource.*

class ArraySourceSignatureTests {
    @TestAttribute
    fun localArraySignaturesKeepSourceClassifiers() {
        checkLocalArraySources()
    }

    @TestAttribute
    fun importedArraySignaturesKeepSourceClassifiers() {
        val edge: ArraySourceEdge<Array<Int>> = IntArraySourceEdge(arrayOf(59))
        check(edge.value()[0] == 59 && readArraySourceEdge(edge)[0] == 59)
        val generic = arrayOf(29, 31)
        val primitive = intArrayOf(37, 41)
        val surface = ArraySourceSurface(generic, primitive, null, arrayOf(generic),
            arrayOf(primitive), arrayOf(43, null))
        val imported: Array<Int> = surface.generic
        val importedPrimitive: IntArray = surface.specialized
        val nested: Array<Array<Int>> = surface.nested
        val mixed: Array<IntArray> = surface.mixed
        val nullableElements: Array<Int?> = surface.nullableElements
        check(imported === generic && importedPrimitive === primitive)
        check(nested[0][1] == 31 && mixed[0][1] == 41)
        check(nullableElements[0] == 43 && nullableElements[1] == null)
        check(surface.echo(generic) === generic && surface.echoSpecialized(primitive) === primitive)
        check(surface.transform(generic) { it } === generic)
        check(surface.optional == null)
        surface.optional = generic
        val optional: Array<Int>? = surface.optional
        check(optional === generic)
        check(GenericArraySource(generic).values === generic)
        check(nestedArraySlot(ArraySourceBox(generic)).value === generic)
        check(charArraySlot(arrayOf('y'))[0] == 'y')
        check(booleanArraySlot(arrayOf(true))[0])
        check(doubleArraySlot(arrayOf(2.5))[0] == 2.5)
        val clrBytes: UByteArray = System.BitConverter.GetBytes(47)
        check(clrBytes.size == 4)
    }
}
