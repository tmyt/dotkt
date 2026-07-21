// Numeric narrow-type battery (migration batch M1) — Byte/Short (signed) args/fields/consts and the widened-return
// arithmetic of narrow operators. Migrates the narrow-numeric family of cases/il-* onto the in-process NUnit suite.
// Each old case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value assert is strictly
// stronger (typed Int/UInt/ULong) than the old text diff. Every value the old il_check asserted is preserved 1:1.
//
// Coverage preserved (old case -> method):
//   il-bytearg   -> byteShortArgs         Byte/Short as params, locals, fields, const args (Int->Byte conv, signed range)
//   il-bytewiden -> narrowArithmeticWidens #93/#71 Byte/Short arith -> Int, UByte/UShort arith -> UInt; inc/dec wrap to
//                                          the receiver's own narrow type; explicit .toUByte()/…/.toULong() conv arms
//
// Batch-M1 collision rule: top-level helpers are `M1`-prefixed (takeByte -> m1TakeByte, Holder -> M1ByteHolder).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

// ---- il-bytearg : Byte/Short parameters, fields ----------------------------------------------------------------
fun m1TakeByte(b: Byte): Int = b.toInt()
fun m1TakeShort(s: Short): Int = s.toInt()
class M1ByteHolder(val b: Byte, val s: Short)

class NumericRepresentationTests {
    @TestAttribute
    fun byteShortArgs() {
        val i = 5
        assertEquals(5, m1TakeByte(i.toByte()))   // 5   (Int->Byte conv arg)
        assertEquals(3, m1TakeByte(3))            // 3   (Byte const arg)
        val bv: Byte = 7
        assertEquals(7, m1TakeByte(bv))           // 7   (Byte local arg)
        assertEquals(9, m1TakeShort(9))           // 9   (Short const arg)
        val h = M1ByteHolder(4, 100)
        assertEquals(4, h.b.toInt())              // 4   (Byte field)
        assertEquals(100, h.s.toInt())            // 100 (Short field)
        val neg: Byte = -2                        // signed range
        assertEquals(-2, m1TakeByte(neg))         // -2
    }

    @TestAttribute
    fun narrowArithmeticWidens() {
        // Signed narrow arithmetic widens to Int (must NOT truncate to the narrow left operand).
        val b1: Byte = 100; val b2: Byte = 100
        assertEquals(200, b1 + b2)          // 200    (Byte+Byte:Int, not -56)
        val s1: Short = 20000; val s2: Short = 20000
        assertEquals(40000, s1 + s2)        // 40000  (Short+Short:Int, not -25536)
        // Unsigned narrow arithmetic widens to UInt.
        val ub1: UByte = 200u; val ub2: UByte = 100u
        assertEquals(300u, ub1 + ub2)       // 300    (UByte+UByte:UInt, not 44)
        val us1: UShort = 40000u; val us2: UShort = 40000u
        assertEquals(80000u, us1 + us2)     // 80000  (UShort+UShort:UInt, not 14464)
        // The common "sum array bytes" pattern (ByteArray element is Byte).
        val arr: ByteArray = byteArrayOf(100, 100)
        assertEquals(200, arr[0] + arr[1])  // 200
        // inc/dec wrap to the narrow type (overflow wraps, like Kotlin/JVM).
        var b: Byte = 127; b++
        assertEquals(-128, b.toInt())       // -128   (Byte.inc overflow wraps)
        var ub: UByte = 255u; ub++
        assertEquals(0, ub.toInt())         // 0      (UByte.inc overflow wraps)
        var us: UShort = 65535u; us++
        assertEquals(0, us.toInt())         // 0      (UShort.inc overflow wraps)
        // Unary minus on Byte widens to Int.
        val mb: Byte = -128
        assertEquals(128, -mb)              // 128    (Byte.unaryMinus:Int, not -128)
        // Explicit unsigned conversions exercise the #71 conv arms (U1/U2/U4/U8).
        val big = 300
        assertEquals(44, big.toUByte().toInt())          // 44     (300 & 0xFF, Conv_U1)
        assertEquals(300, big.toUShort().toInt())        // 300    (Conv_U2)
        assertEquals(4294967295u, (-1).toUInt())         // 4294967295            (Conv_U4)
        assertEquals(18446744073709551615uL, (-1).toULong()) // 18446744073709551615 (Conv_U8)
    }
}
