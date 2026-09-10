import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

open class OwnerBoundSink<in T> {
    open fun <R : T> render(value: R): String = "base:$value"
    fun <R : T> echo(value: R): R = value
    fun <R : T> withCallback(value: R, callback: (R) -> String): String = callback(value)
    fun <R : S, S : T> transitive(value: R): String = value.toString()
}

class OwnerBoundDerived : OwnerBoundSink<Any>() {
    override fun <R : Any> render(value: R): String = "derived:$value"
}

interface OwnerBoundContract<in T> {
    fun <R : T> invoke(value: R): String
}

class OwnerBoundContractImpl : OwnerBoundContract<Any> {
    override fun <R : Any> invoke(value: R): String = "interface:$value"
}

class ConstrainedCarrierTests {
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
    }
}
