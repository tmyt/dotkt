import GenericArrayBoundaries.Api
import GenericArrayBoundaries.Cell
import GenericArrayBoundaries.Transform
import GenericArrayBoundaries.JaggedTransform
import NUnit.Framework.TestAttribute
import kotlin.clr.byref

private class GenericArrayEcho : Transform {
    override fun <T> Apply(values: Array<T>): Array<T> = values
}

private class GenericJaggedArrayEcho : JaggedTransform {
    override fun <T> Apply(values: Array<Array<T>>): Array<Array<T>> = values
}

private fun <T> checkNativeJaggedArrays(values: Array<Array<T>>, replacement: Array<Array<T>>) {
    val nested = System.Collections.Generic.List<Array<Array<T>>>()
    nested.Add(values)
    check(Api.NestedJagged(nested) === values)
    check(Api.InvokeJagged(GenericJaggedArrayEcho(), values) === values)
    var slot = values
    Api.ReplaceJagged(byref(slot), replacement)
    check(slot === replacement)
    val cell = Cell(values)
    var live by byref(Api.Live(cell))
    live = replacement
    check(cell.Value === replacement)
}

private fun <T> checkNativeArrayBoundaries(values: Array<T>, replacement: Array<T>, element: T) {
    check(Api.Echo(values) === values)
    var slot = values
    Api.Replace(byref(slot), replacement)
    check(slot === replacement)
    val cell = Cell(values)
    var live by byref(Api.Live(cell))
    live = replacement
    check(cell.Value === replacement)
    var first by byref(Api.Element(values, 0))
    first = element
    check(values[0] === element || values[0] == element)
    val nested = System.Collections.Generic.List<Array<T>>()
    nested.Add(values)
    check(Api.Nested(nested) === values)
    check(Api.Invoke(GenericArrayEcho(), values) === values)
}

class GenericArrayBoundaryTests {
    @TestAttribute
    fun nativeGenericArraysKeepExactSignaturesAndLiveLocations() {
        checkNativeArrayBoundaries(arrayOf("first"), arrayOf("replacement"), "changed")
        checkNativeArrayBoundaries(arrayOf(1), arrayOf(2), 3)
        checkNativeJaggedArrays(arrayOf(arrayOf("first")), arrayOf(arrayOf("replacement")))
        checkNativeJaggedArrays(arrayOf(arrayOf(1)), arrayOf(arrayOf(2)))
    }
}
