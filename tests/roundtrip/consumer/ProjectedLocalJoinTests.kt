package roundtrip.consumer

import NUnit.Framework.TestAttribute
import roundtrip.localjoin.LocalJoinHost

class ProjectedLocalJoinTests {
    @TestAttribute
    fun differentLocalConstructionsKeepTheirValuesAcrossTheJoin() {
        check(LocalJoinHost("outer").value(true) == 12)
        check(LocalJoinHost(41).value(false) == "text")
    }
}
