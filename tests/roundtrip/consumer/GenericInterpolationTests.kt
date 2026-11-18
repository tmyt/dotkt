package roundtriptests.interpolation

import NUnit.Framework.TestAttribute
import roundtrip.interpolation.*

private fun <T> localRender(value: T): String = "[$value]"
private class Description(val describe: () -> String) {
    override fun toString(): String = describe()
}
private fun <T> mixed(first: Description, second: T): String = "[$first:$second]"
@Suppress("UNCHECKED_CAST")
private fun <T> safeRender(value: Any?): String = "[${value as? T}]"

class GenericInterpolationTests {
    @TestAttribute fun genericValuesUseKotlinNullTextAcrossAssemblies() {
        check(localRender<String?>(null) == "[null]")
        check(renderValue<Int?>(null) == "[null]")
        check(renderInline<String?>(null) == "[null]")
        check(renderNullable<Int>(null) == "[null]" && renderText(null) == "[null]")
        check(renderValue(42) == "[42]" && renderNullable<Int>(7) == "[7]")
        check(renderValue("text") == "[text]")
        check(safeRender<String>(null) == "[null]")
        check("[${System.Threading.ThreadLocal<String>().Value}]" == "[null]")
        check("[${System.Threading.ThreadLocal<Int>().Value}]" == "[0]")
        var calls = 0
        check(RenderSource<String?>(null).renderWith { calls++; null } == "[null:null]")
        check(calls == 1)
    }

    @TestAttribute fun interpolationRendersOnceInSourceOrderAndPropagatesExceptions() {
        var order = ""
        val first = Description { order += "a"; "one" }
        val second = Description { order += "c"; "two" }
        check(RenderSource(first).renderWith { order += "b"; second } == "[one:two]")
        check(order == "abc")
        val expected = IllegalStateException("description")
        val failure = try {
            renderValue(Description { throw expected })
            null
        } catch (e: IllegalStateException) { e }
        check(failure === expected)
        val other = IllegalStateException("second")
        val mixedFailure = try {
            mixed(Description { throw expected }, Description { throw other })
            null
        } catch (e: IllegalStateException) { e }
        check(mixedFailure === expected)
    }
}
