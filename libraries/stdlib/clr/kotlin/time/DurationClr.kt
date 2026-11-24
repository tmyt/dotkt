package kotlin.time

// Matches the JS actual: duration self-assertions are always enabled in this build.
internal actual val durationAssertionsEnabled: Boolean get() = true

internal actual fun formatToExactDecimals(value: Double, decimals: Int): String {
    require(decimals in 0..12)
    val bits = value.toRawBits()
    val exponent = ((bits ushr 52) and 0x7ffL).toInt()
    val fraction = bits and 0x000f_ffff_ffff_ffffL
    val sign = if (bits < 0) "-" else ""
    if (exponent == 0x7ff) return if (fraction != 0L) "NaN" else sign + "Infinity"

    // abs(value) * 10^d = significand * 5^d * 2^(exponent - bias - 52 + d).
    // A subnormal has no implicit leading bit and uses the smallest normal exponent.
    val significand = if (exponent == 0) fraction else fraction or (1L shl 52)
    val shift = maxOf(exponent, 1) - 1023 - 52 + decimals
    val magnitude = DurationMagnitude(significand, 53 + 3 * decimals + maxOf(shift, 0))
    var fives = 1
    for (i in 0 until decimals) fives *= 5
    magnitude.multiply(fives)
    magnitude.scalePowerOfTwo(shift)
    val digits = magnitude.digits().padStart(decimals + 1, '0')
    if (decimals == 0) return sign + digits
    val point = digits.length - decimals
    return sign + digits.substring(0, point) + "." + digits.substring(point)
}

// Little-endian decimal limbs: each holds at least 29 bits; a limb times
// 2^29 (or 5^12) plus carry fits in a signed Long.
private const val DURATION_LIMB_BASE = 1_000_000_000

private class DurationMagnitude(value: Long, maxBits: Int) {
    private val limbs = IntArray((maxBits + 28) / 29)
    private var size = 1

    init {
        limbs[0] = (value % DURATION_LIMB_BASE).toInt()
        if (value >= DURATION_LIMB_BASE) {
            limbs[1] = (value / DURATION_LIMB_BASE).toInt()
            size = 2
        }
    }

    fun multiply(factor: Int) {
        var carry = 0L
        for (i in 0 until size) {
            val product = limbs[i].toLong() * factor + carry
            limbs[i] = (product % DURATION_LIMB_BASE).toInt()
            carry = product / DURATION_LIMB_BASE
        }
        if (carry != 0L) limbs[size++] = carry.toInt()
    }

    private fun divide(divisor: Int): Int {
        var remainder = 0L
        for (i in size - 1 downTo 0) {
            val dividend = remainder * DURATION_LIMB_BASE + limbs[i]
            limbs[i] = (dividend / divisor).toInt()
            remainder = dividend % divisor
        }
        while (size > 1 && limbs[size - 1] == 0) size--
        return remainder.toInt()
    }

    fun scalePowerOfTwo(shift: Int) {
        var remaining = shift
        while (remaining > 0) {
            val n = minOf(29, remaining)
            multiply(1 shl n)
            remaining -= n
        }
        var sticky = false
        while (remaining < 0 && (size > 1 || limbs[0] != 0)) {
            val n = minOf(29, -remaining)
            val divisor = 1 shl n
            val remainder = divide(divisor)
            remaining += n
            if (remaining == 0 && (remainder > divisor / 2 ||
                    remainder == divisor / 2 && (sticky || limbs[0] % 2 != 0))) {
                var i = 0
                while (i < size && limbs[i] == DURATION_LIMB_BASE - 1) limbs[i++] = 0
                if (i == size) size++
                limbs[i]++
            }
            sticky = sticky || remainder != 0
        }
    }

    fun digits(): String = buildString {
        append(limbs[size - 1])
        for (i in size - 2 downTo 0) append(limbs[i].toString().padStart(9, '0'))
    }
}
