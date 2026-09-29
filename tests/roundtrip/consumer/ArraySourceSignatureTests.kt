@file:OptIn(kotlin.ExperimentalUnsignedTypes::class)

package roundtriptests.arraysource

import NUnit.Framework.TestAttribute
import roundtrip.arraysource.*
import kotlin.coroutines.*

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
        val unsigned: Array<UInt> = unsignedArraySlot(arrayOf(71u))
        val unsignedPrimitive: UIntArray = unsignedSpecializedSlot(uintArrayOf(73u))
        check(unsigned[0] == 71u && unsignedPrimitive[0] == 73u)
        topLevelArray = generic
        val top: Array<Int> = topLevelArray
        check(top === generic && top.firstArraySource() == 29)
        val nestedEdge: ArraySourceEdge<Array<ArraySourceOuter.Nested>> = NestedArrayEdge()
        val innerEdge: ArraySourceEdge<Array<ArraySourceOuter<String>.Inner<Int>>> = InnerArrayEdge()
        check(nestedEdge.value().isEmpty() && innerEdge.value().isEmpty())
        check(ArrayBoundHolder(InnerArrayEdge()).source.value().isEmpty())
        check(keepArrayBound(InnerArrayEdge()).value().isEmpty())
        val inherited: ArraySourceOuter<String>.Inner<Array<Int>> = ArraySourceOuter<String>().Sub(generic)
        check(inherited.value[0] == 29)
        val star: GenericArraySource<*> = GenericArraySource(arrayOf("a", "b"))
        check(star.values.size == 2)
        var completed: Array<Int>? = null
        val suspendBlock: suspend () -> Array<Int> = { suspendArraySource() }
        suspendBlock.startCoroutine(object : Continuation<Array<Int>> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(result: Result<Array<Int>>) { completed = result.getOrThrow() }
        })
        check(completed?.get(0) == 67)
        val relayed: suspend (Array<Int>) -> String? = relayArraySource<String> { null }
        var relayedCompleted = false
        val invokeRelayed: suspend () -> String? = { relayed(generic) }
        invokeRelayed.startCoroutine(object : Continuation<String?> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(result: Result<String?>) {
                check(result.getOrThrow() == null)
                relayedCompleted = true
            }
        })
        check(relayedCompleted)
        val clrBytes: UByteArray = System.BitConverter.GetBytes(47)
        check(clrBytes.size == 4)
    }
}
