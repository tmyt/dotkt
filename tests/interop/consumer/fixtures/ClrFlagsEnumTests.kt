import FlagsInterop.AccessFlags
import FlagsInterop.ByteFlags
import FlagsInterop.FlagsApi
import FlagsInterop.Int16Flags
import FlagsInterop.Int32Flags
import FlagsInterop.Int64Flags
import FlagsInterop.SByteFlags
import FlagsInterop.UInt16Flags
import FlagsInterop.UInt32Flags
import FlagsInterop.UInt64Flags
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class ClrFlagsEnumTests {
    private var evaluationCount = 0
    private var firstEvaluation = ""
    private var secondEvaluation = ""

    private fun evaluated(label: String, value: AccessFlags): AccessFlags {
        if (evaluationCount == 0) firstEvaluation = label else secondEvaluation = label
        evaluationCount++
        return value
    }

    private fun resetEvaluations() {
        evaluationCount = 0
        firstEvaluation = ""
        secondEvaluation = ""
    }

    @TestAttribute
    fun typedOperationsAcceptNamedUnnamedAndUnknownBitPatterns() {
        val readWrite: AccessFlags = AccessFlags.Read or AccessFlags.Write
        assertEquals(AccessFlags.ReadWrite, readWrite)
        assertEquals(AccessFlags.Read, readWrite and AccessFlags.Read)
        assertEquals(AccessFlags.Write, readWrite xor AccessFlags.Read)
        assertEquals(AccessFlags.Read, AccessFlags.AliasRead)

        assertEquals(true, AccessFlags.Read in readWrite)
        assertEquals(true, AccessFlags.ReadWrite in readWrite)
        assertEquals(false, AccessFlags.Execute in readWrite)
        assertEquals(true, AccessFlags.None in AccessFlags.Execute)

        val unknown = FlagsApi.Unknown()
        val combined: AccessFlags = unknown or AccessFlags.Read
        assertEquals(9, FlagsApi.Bits(FlagsApi.RoundTrip(combined)))
    }

    @TestAttribute
    fun everyUnderlyingWidthPreservesHighBitsAndTruncatesComplement() {
        assertEquals(SByteFlags.High, SByteFlags.None or SByteFlags.High)
        assertEquals(SByteFlags.NotLow, SByteFlags.Low.inv())
        assertEquals(ByteFlags.High, ByteFlags.None or ByteFlags.High)
        assertEquals(ByteFlags.NotLow, ByteFlags.Low.inv())
        assertEquals(Int16Flags.High, Int16Flags.None or Int16Flags.High)
        assertEquals(Int16Flags.NotLow, Int16Flags.Low.inv())
        assertEquals(UInt16Flags.High, UInt16Flags.None or UInt16Flags.High)
        assertEquals(UInt16Flags.NotLow, UInt16Flags.Low.inv())
        assertEquals(Int32Flags.High, Int32Flags.None or Int32Flags.High)
        assertEquals(Int32Flags.NotLow, Int32Flags.Low.inv())
        assertEquals(UInt32Flags.High, UInt32Flags.None or UInt32Flags.High)
        assertEquals(UInt32Flags.NotLow, UInt32Flags.Low.inv())
        assertEquals(Int64Flags.High, Int64Flags.None or Int64Flags.High)
        assertEquals(Int64Flags.NotLow, Int64Flags.Low.inv())
        assertEquals(UInt64Flags.High, UInt64Flags.None or UInt64Flags.High)
        assertEquals(UInt64Flags.NotLow, UInt64Flags.Low.inv())
    }

    @TestAttribute
    fun operandsAreEvaluatedOnceInCallOrder() {
        resetEvaluations()
        val combined = evaluated("receiver", AccessFlags.Read) or evaluated("argument", AccessFlags.Write)
        assertEquals(AccessFlags.ReadWrite, combined)
        assertEquals(2, evaluationCount)
        assertEquals("receiver", firstEvaluation)
        assertEquals("argument", secondEvaluation)

        resetEvaluations()
        assertEquals(
            true,
            evaluated("requested", AccessFlags.Read) in evaluated("flags", AccessFlags.ReadWrite),
        )
        assertEquals(2, evaluationCount)
        assertEquals("flags", firstEvaluation)
        assertEquals("requested", secondEvaluation)
    }
}
