package roundtriptests.unsignedportable

import NUnit.Framework.TestAttribute
import roundtrip.unsignedportable.*

class UnsignedPortableTests {
    @TestAttribute fun libraryMethodsAndReferencesKeepUnsignedFrames() {
        val ui = UInt.MAX_VALUE
        val ul = ULong.MAX_VALUE
        for (radix in 2..36) {
            check(formatUInt(ui, radix).toUInt(radix) == ui)
            check(formatULong(ul, radix).toULong(radix) == ul)
        }
        check(formatUInt(ui, 36) == "1z141z3")
        check(formatULong(ul, 36) == "3w5e11264sgsf")
        val f = savedUIntFormatter()
        val g = savedULongFormatter()
        check(f(ui, 16) == "ffffffff")
        check(g(ul, 16) == "ffffffffffffffff")
        check(nativeUIntMax(ui, 1u) == ui)
        check(nativeULongMax(ul, 1uL) == ul)
    }

    @TestAttribute fun importedInlineBodiesKeepSaturationAndSingleEvaluation() {
        var calls = 0
        check(observedFormat(ULong.MAX_VALUE, 36) { calls++; it } == "3w5e11264sgsf")
        check(observedUInt(Double.NaN) { calls++; it } == 0u)
        check(observedULong(Double.POSITIVE_INFINITY) { calls++; it } == ULong.MAX_VALUE)
        check(calls == 3)
    }
}
