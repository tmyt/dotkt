package roundtriptests.storedsam

import NUnit.Framework.TestAttribute
import System.Threading.ThreadStart
import roundtrip.storedsam.*

class StoredSamConversionTests {
    @TestAttribute
    fun conversionsSurviveImportedInlineAndDefaultCarriers() {
        var calls = 0
        val callback: () -> Unit = { calls++ }
        val inline = inlineThreadStart(callback)
        val default = defaultThreadStart(callback)
        val returned = ThreadStart(returnedCallback(callback))
        check(calls == 0)
        inline()
        default()
        returned()
        check(calls == 3)
    }
}
