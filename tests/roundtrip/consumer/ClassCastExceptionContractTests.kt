package roundtriptests.castcontract

import NUnit.Framework.TestAttribute
import roundtrip.castcontract.CastExceptionApi

class ClassCastExceptionContractTests {
    @TestAttribute
    fun importedCastsHelpersAndFactoriesShareTheirExceptionIdentity() {
        val api = CastExceptionApi()
        var concrete = false
        try { api.failValue("wrong") } catch (_: ClassCastException) { concrete = true }
        check(concrete)
        var classifier = false
        try { api.failClassifier("wrong") } catch (_: System.InvalidCastException) { classifier = true }
        check(classifier)
        var explicit = false
        try { api.failAsKotlin("cross-DLL") } catch (failure: System.InvalidCastException) {
            check(failure.Message == "cross-DLL")
            explicit = true
        }
        check(explicit)
        val exported: Any = api.construct("factory")
        check(exported is System.InvalidCastException)
        check(api.construct("source view").message == "source view")
    }
}
