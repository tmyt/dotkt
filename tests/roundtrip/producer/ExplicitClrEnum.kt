package roundtrip.clrenum

import kotlin.clr.ClrEnum

@ClrEnum
enum class OrderedCode(value: Int) {
    FIRST(10),
    NEGATIVE(-4),
    ZERO(0);

    companion object {
        fun marker(): Int = 526
    }
}

fun classifyOrderedCode(value: OrderedCode): String = when (value) {
    OrderedCode.FIRST -> "first"
    OrderedCode.NEGATIVE -> "negative"
    OrderedCode.ZERO -> "zero"
}
