package roundtriptests.portablecoreactual

import NUnit.Framework.TestAttribute
import roundtrip.portablecoreactual.*

class PortableCoreActualTests {
    @TestAttribute fun importedResourceUsesTheStdlibContractAndNativeDisposeSlot() {
        var closes = 0
        val assigned: String
        val resource = importedCloseable { closes++ }
        resource.use { assigned = "assigned in consumer" }
        check(assigned == "assigned in consumer" && closes == 1)
        (resource as System.IDisposable).Dispose()
        check(closes == 2)
        check(importedUseAssignment() == "assigned in producer")
    }

    @TestAttribute fun importedPublicationMemoizesNullAcrossTheDllBoundary() {
        val value = importedNullablePublication()
        check(!value.isInitialized())
        check(value.value == null && value.value == null)
        check(value.isInitialized() && value.toString() == "null")
    }
}
