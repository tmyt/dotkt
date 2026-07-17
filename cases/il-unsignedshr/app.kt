// #94: unsigned `shr` must be LOGICAL (zero-filling). Kotlin's UInt.shr/ULong.shr zero-fill; the signed
// arithmetic `>>` sign-propagates. bir2cir lowers an unsigned-owner `shr` to ">>>" (ilemit Shr_Un). `shl` is
// bit-identical for signed/unsigned, and signed `shr` stays arithmetic — both are non-regression checks here.
fun main() {
    println(UInt.MAX_VALUE shr 1)       // 2147483647            (0xFFFFFFFF >>> 1)
    println(ULong.MAX_VALUE shr 1)      // 9223372036854775807   (0xFFFF...F >>> 1)
    println(0xFF000000u shr 4)          // 267386880             (0x0FF00000)
    println(2147483648u shr 1)          // 1073741824            (high-bit set must zero-fill, not sign-extend)
    println(1u shl 31)                  // 2147483648            (shl bit-identical signed/unsigned)
    println((-8) shr 1)                 // -4                    (signed Int shr stays arithmetic)
}
