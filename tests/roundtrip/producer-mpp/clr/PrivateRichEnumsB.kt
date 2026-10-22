package roundtrip.enumcollision

private enum class UnusedState(val code: Int) { READY(1), DONE(2), FAILED(3) }
private enum class RichLocalState(val code: Int) { READY(30), DONE(31), FAILED(32) }
private enum class BasicLocalState(val code: Int) { READY(40), DONE(41), FAILED(42) }

fun producerBState(): Int = RichLocalState.READY.code
