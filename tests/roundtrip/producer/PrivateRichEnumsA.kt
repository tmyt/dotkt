package roundtrip.enumcollision

private enum class UnusedState(val code: Int) {
    OFF(0), ON(1);
    fun label(prefix: String = "a"): String = prefix + code
    inline fun <R> map(f: (Int) -> R): R = f(code)
}
private enum class RichLocalState(val code: Int) { FIRST(10), SECOND(11) }
private enum class BasicLocalState(val code: Int) { FIRST(20), SECOND(21) }

@PublishedApi
internal enum class PublishedState(val code: Int) { EXPORTED(73) }

inline fun publishedEnumCode(block: (Int) -> Int): Int = block(PublishedState.EXPORTED.code)
fun producerAState(): Int = RichLocalState.FIRST.code
fun producerADefault(): String = UnusedState.OFF.label()
