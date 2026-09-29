package roundtriptests.innerresults

import NUnit.Framework.TestAttribute
import roundtrip.innerresults.*

fun <A, B> checkImportedInnerResult(own: A, value: B) {
    val child = GenericResult<A, B>().Child(own, value)
    check(child.own == own && child.value == value)
    check(child.readOwn() == own && child.readValue() == value)
    check(child.nullableOwn() == own && child.nullableValue() == value)
    check(child.ownCell().value == own && child.valueCell().value == value)
}

class ImportedResultHost<A, B> {
    fun check(own: A, value: B) {
        val child = GenericResult<A, B>().Child(own, value)
        kotlin.check(child.own == own && child.value == value)
        kotlin.check(child.readOwn() == own && child.readValue() == value)
        kotlin.check(child.nullableOwn() == own && child.nullableValue() == value)
        kotlin.check(child.ownCell().value == own && child.valueCell().value == value)
    }
}

class InheritedInnerResultTests {
    @TestAttribute
    fun closedResultsKeepDistinctOwnAndEnclosingTypes() {
        checkLocalInnerResults()
        val child = ClosedResult().Child()
        check(child.value == "value" && child.own == 17)
        check(child.readValue() == "value" && child.readOwn() == 17)
    }

    @TestAttribute
    fun inheritedResultsCloseIntoCallerGenericFrames() {
        checkImportedInnerResult("own", 23)
        checkImportedInnerResult(31, "outer")
        checkImportedInnerResult<Int?, String?>(null, null)
        checkImportedInnerResult<Int?, String?>(37, "present")
        ImportedResultHost<Int, String>().check(43, "class")
        ImportedResultHost<String, Int>().check("own", 47)
        ImportedResultHost<String?, Int?>().check(null, null)
    }
}
