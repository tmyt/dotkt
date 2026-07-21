// C#-producer roundtrip consumer battery B — UNSIGNED .NET surface. Consumes the plain C# producer's public API
// façade-free (facadegen re-imports the built dll's types from `import <Ns>.<Type>`; DLL-not-source) and asserts
// each migrated case's golden values 1:1 as `// <expected>` trailing comments (design D1).
//   injuint  <- il-injuint   .NET UNSIGNED params: System.UInt32 -> kotlin.UInt, System.UInt64 -> kotlin.ULong
//   ubyteinj <- il-ubyteinj  System.Byte -> kotlin.UByte, byte[] -> UByteArray (STRICT, #53)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
// il-injuint
import Boot.Strap
// il-ubyteinj
import Bt.B

class UnsignedInteropTests {
    @TestAttribute
    fun injuint() {
        assertEquals(65542, Strap.Initialize(0x00010006u))  // 65542 — 1.6 packed (major 0x0001, minor 0x0006)
        assertEquals(42L, Strap.Big(41uL))                  // 42 — ULong param, +1
    }

    @OptIn(kotlin.ExperimentalUnsignedTypes::class)
    @TestAttribute
    fun ubyteinj() {
        val u: UByte = B.One()
        assertEquals(200, u.toInt())          // 200 — System.Byte read as UByte 200 (NOT signed -56)
        val a: UByteArray = B.Arr()
        assertEquals(3, a.size)               // 3 — byte[] surfaced as UByteArray
        assertEquals(250, a[2].toInt())       // 250
        assertEquals(200, B.Take(200u))       // 200 — pass a UByte to a System.Byte param
        assertEquals(253, B.TakeArr(a))       // 253 — pass a UByteArray to a System.Byte[] param (3 + 250)
    }
}
