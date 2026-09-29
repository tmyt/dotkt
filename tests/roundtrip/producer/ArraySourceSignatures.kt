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
