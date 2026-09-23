import NUnit.Framework.TestAttribute
import roundtrip.defaultlambdaframes.DefaultOwner

private class DefaultFrameCaller<A : CharSequence, B>(val label: A, val value: B) {
    fun read(): B = DefaultOwner(value).read()
    fun nested(): B = DefaultOwner(value).nested()
    fun inlineRead(): B = DefaultOwner(value).inlineRead({ check(label.length > 0); it })
    fun <M> method(value: M): M = DefaultOwner(this.value).method(value)
    fun sam(): B = DefaultOwner(value).sam()
    fun <M> inlineMethod(value: M): M = DefaultOwner(this.value).inlineMethod(value, { check(label.length > 0); it })
}

private class DefaultFrameValue<T>(val value: T)

private fun <A, B> methodCaller(first: A, second: B): B = DefaultOwner(first).method(second)

private fun <A, B> inlineMethodCaller(first: A, second: B): B = DefaultOwner(first).inlineMethod(second, { it })

private fun <M> constructedCaller(value: M): M = DefaultOwner(DefaultFrameValue(value)).read().value

class DefaultLambdaFrameRoundtripTests {
    @TestAttribute
    fun importedDefaultLambdasKeepReorderedOwnerSlots() {
        val caller = DefaultFrameCaller("label", 23)
        check(caller.read() == 23)
        check(caller.nested() == 23)
        check(caller.inlineRead() == 23)
        check(caller.method("method") == "method")
        check(caller.sam() == 23)
        check(caller.inlineMethod("inline method") == "inline method")
    }

    @TestAttribute
    fun importedDefaultLambdasKeepMethodAndConstructedFrames() {
        check(methodCaller("unused", 29) == 29)
        check(methodCaller(17, "value") == "value")
        check(inlineMethodCaller("unused", 43) == 43)
        check(inlineMethodCaller(17, "inline") == "inline")
        check(constructedCaller(31) == 31)
        check(constructedCaller("constructed") == "constructed")
    }

    @TestAttribute
    fun importedDefaultLambdasKeepConcreteAndNullableResults() {
        check(DefaultOwner(37).read() == 37)
        check(DefaultOwner(41).sam() == 41)
        check(DefaultOwner("text").nested() == "text")
        check(DefaultOwner<String?>(null).read() == null)
        check(DefaultFrameCaller<String, Int?>("nullable", null).read() == null)
    }
}
