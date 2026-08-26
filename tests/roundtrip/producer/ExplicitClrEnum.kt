package roundtrip.clrenum

import System.FlagsAttribute
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

@FlagsAttribute
@ClrEnum
enum class RoundtripAccess(value: UInt) {
    NONE(0u),
    READ(1u),
    WRITE(2u),
    READ_WRITE(3u),
}
