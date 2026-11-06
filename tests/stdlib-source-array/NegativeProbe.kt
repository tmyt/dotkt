package kotlin

// Minimal source-owned builtin for frontend-only calibration; this input is never emitted.
class CharArray

private class NativeArrayBound<T : System.Array>
private fun invalidSourceArrayBound(value: NativeArrayBound<String>) {}

private open class OrdinaryBase
@kotlin.jvm.JvmInline
private value class InvalidSourceValueClass(val value: Int) : OrdinaryBase()

private fun invalidSourceErrorNode(text: String?): Int = text.length
private fun invalidSourceControlFlow(flag: Boolean): Int {
    if (flag) return 1
}
