package kotlin

// Minimal source-owned builtin for frontend-only calibration; this input is never emitted.
class CharArray

private class NativeArrayBound<T : System.Array>
private fun invalidSourceArrayBound(value: NativeArrayBound<String>) {}

private interface NativeArrayProducer { fun read(): System.Array }
private class InvalidArrayProducer : NativeArrayProducer {
    override fun read(): String = "not an array"
}

private open class OrdinaryBase
@kotlin.jvm.JvmInline
private value class InvalidSourceValueClass(val value: Int) : OrdinaryBase()

private fun invalidSourceErrorNode(text: String?): Int = text.length
private fun invalidSourceControlFlow(flag: Boolean): Int {
    if (flag) return 1
}
