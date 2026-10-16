@file:OptIn(kotlin.ExperimentalUnsignedTypes::class)

package roundtrip.arraysource

class ArraySourceSurface(
    val generic: Array<Int>,
    val specialized: IntArray,
    var optional: Array<Int>?,
    val nested: Array<Array<Int>>,
    val mixed: Array<IntArray>,
    val nullableElements: Array<Int?>,
) {
    fun echo(values: Array<Int>): Array<Int> = values
    fun echoSpecialized(values: IntArray): IntArray = values
    fun transform(values: Array<Int>, block: (Array<Int>) -> Array<Int>): Array<Int> = block(values)
}

class GenericArraySource<T>(val values: Array<T>)
annotation class ArraySourceNames(vararg val names: String)
fun firstStarArray(values: Array<*>): Any? = values[0]
@ArraySourceNames("star", "array")
fun relayStarArray(values: Array<*>): Array<*> = values
fun <T> genericStarArray(values: Array<T>): Array<*> = values
fun nullableStarArray(values: Array<*>?): Array<*>? = values
fun castStarArray(value: Any?): Array<*>? = value as? Array<*>
fun projectedStarArray(values: Array<out Any?>): Array<*> = values
fun writeProjectedArray(values: Array<in Int>, value: Int) { values[0] = value }
fun castWritableIntArray(value: Any): Array<in Int>? = value as? Array<in Int>
fun isStarArray(value: Any?): Boolean = value is Array<*>
fun isNullableStarArray(value: Any?): Boolean = value is Array<*>?
fun castNestedStarArray(value: Any): Array<Array<*>>? = value as? Array<Array<*>>

fun checkLocalStarArraySources() {
    val values = arrayOf(7, 8)
    check(firstStarArray(values) == 7)
    val projected = relayStarArray(values)
    check(projected === values && genericStarArray(values) === values)
    values[0] = 9
    check(projected[0] == 9 && projected.iterator().next() == 9)
    check(projectedStarArray(values) === values)
    writeProjectedArray(values, 11)
    check(projected[0] == 11)
    val source: GenericArraySource<*> = GenericArraySource(values)
    check(source.values === values && source.values[0] == 11)
    check(nullableStarArray(values) === values && nullableStarArray(null) == null)
    check(castStarArray(values) === values)
    check(castStarArray(null) == null && castStarArray("not an array") == null)
    check(isStarArray(values) && !isStarArray(null) && !isStarArray("not an array"))
    check(isNullableStarArray(values) && isNullableStarArray(null) && !isNullableStarArray("not an array"))
    val nullable = arrayOf<Int?>(null, 13)
    check(relayStarArray(nullable) === nullable && firstStarArray(nullable) == null)
    nullable[0] = 17
    check(relayStarArray(nullable)[0] == 17)
    val references = arrayOf("a", "b")
    check(genericStarArray(references) === references && firstStarArray(references) == "a")
    references[0] = "changed"
    check(relayStarArray(references)[0] == "changed")
    check(castWritableIntArray(references) === references)
    val objects = arrayOf<Any?>(null)
    writeProjectedArray(objects, 19)
    check(objects[0] == 19)
    val nested = arrayOf(arrayOf(23))
    check(relayStarArray(nested) === nested)
    check((firstStarArray(nested) as Array<*>)[0] == 23)
    val openNested: Any = arrayOf<Array<*>>(arrayOf(29), arrayOf("nested"))
    check(castNestedStarArray(openNested) === openNested)
    check((openNested as Array<Array<*>>)[0][0] == 29)
    check(castNestedStarArray(arrayOf("not nested")) == null)
}

class ArraySourceBox<T>(val value: T)
interface ArraySourceEdge<T> { fun value(): T }
class IntArraySourceEdge(val items: Array<Int>) : ArraySourceEdge<Array<Int>> {
    override fun value(): Array<Int> = items
}
fun <T : ArraySourceEdge<Array<Int>>> readArraySourceEdge(edge: T): Array<Int> = edge.value()
fun nestedArraySlot(value: ArraySourceBox<Array<Int>>): ArraySourceBox<Array<Int>> = value
fun charArraySlot(value: Array<Char>): Array<Char> = value
fun booleanArraySlot(value: Array<Boolean>): Array<Boolean> = value
fun doubleArraySlot(value: Array<Double>): Array<Double> = value
fun unsignedArraySlot(value: Array<UInt>): Array<UInt> = value
fun unsignedSpecializedSlot(value: UIntArray): UIntArray = value
var topLevelArray: Array<Int> = arrayOf(61)
fun Array<Int>.firstArraySource(): Int = this[0]
suspend fun suspendArraySource(): Array<Int> = arrayOf(67)
fun <T> relayArraySource(block: suspend (Array<Int>) -> T?): suspend (Array<Int>) -> T? = block

class ArraySourceOuter<A> {
    class Nested
    open inner class Inner<B>(val value: B)
    inner class Sub(values: Array<Int>) : Inner<Array<Int>>(values)
}
class NestedArrayEdge : ArraySourceEdge<Array<ArraySourceOuter.Nested>> {
    override fun value(): Array<ArraySourceOuter.Nested> = emptyArray()
}
class InnerArrayEdge : ArraySourceEdge<Array<ArraySourceOuter<String>.Inner<Int>>> {
    override fun value(): Array<ArraySourceOuter<String>.Inner<Int>> = emptyArray()
}
class ArrayBoundHolder<T : ArraySourceEdge<Array<ArraySourceOuter<String>.Inner<Int>>>>(val source: T)
fun <T : ArraySourceEdge<Array<ArraySourceOuter<String>.Inner<Int>>>> keepArrayBound(value: T): T = value

fun checkLocalArraySources() {
    val edge: ArraySourceEdge<Array<Int>> = IntArraySourceEdge(arrayOf(53))
    check(readArraySourceEdge(edge)[0] == 53)
    val generic = arrayOf(11, 13)
    val primitive = intArrayOf(17, 19)
    val surface = ArraySourceSurface(generic, primitive, null, arrayOf(generic),
        arrayOf(primitive), arrayOf(23, null))
    check(surface.generic === generic && surface.specialized === primitive)
    check(surface.echo(generic) === generic && surface.echoSpecialized(primitive) === primitive)
    check(surface.transform(generic) { it } === generic)
    check(surface.nested[0][1] == 13 && surface.mixed[0][1] == 19)
    check(surface.nullableElements[0] == 23 && surface.nullableElements[1] == null)
    surface.optional = generic
    check(surface.optional === generic)
    check(GenericArraySource(generic).values === generic)
    check(nestedArraySlot(ArraySourceBox(generic)).value === generic)
    check(charArraySlot(arrayOf('x'))[0] == 'x')
    check(booleanArraySlot(arrayOf(true))[0])
    check(doubleArraySlot(arrayOf(1.5))[0] == 1.5)
}
