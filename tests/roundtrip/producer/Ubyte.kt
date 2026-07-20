// Migrated verify-roundtrip.sh section `roundtrip-ubyte` — the library half.
// UByte/UByteArray strict-mapping fidelity: a `UByte` return emits a `System.Byte` which facadegen must surface
// back as `UByte` (a mis-restored signed Byte prints -56 for 200); a `UByteArray` return emits `System.Byte[]`
// which must surface as `UByteArray` (not ByteArray / Array<UByte>). The consumer's compile-dependency (`val u:
// UByte = ub()`) is the sharp signal.
@file:OptIn(kotlin.ExperimentalUnsignedTypes::class)
package roundtrip.ubyte

fun ub(): UByte = 200u                                   // emits System.Byte 200 -> facadegen surfaces as UByte
fun uba(): UByteArray = ubyteArrayOf(1u, 2u, 250u)       // emits System.Byte[] -> facadegen surfaces as UByteArray
fun takeUb(x: UByte): Int = x.toInt()                    // System.Byte param -> facadegen surfaces as UByte
