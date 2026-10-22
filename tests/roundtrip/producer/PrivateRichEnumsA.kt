package roundtrip.enumcollision

private enum class UnusedState(val code: Int) { OFF(0), ON(1) }
private enum class RichLocalState(val code: Int) { FIRST(10), SECOND(11) }
private enum class BasicLocalState(val code: Int) { FIRST(20), SECOND(21) }

@PublishedApi
internal enum class PublishedState(val code: Int) { EXPORTED(73) }

inline fun publishedEnumCode(): Int = PublishedState.EXPORTED.code
fun producerAState(): Int = RichLocalState.FIRST.code
