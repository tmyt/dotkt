import NUnit.Framework.TestAttribute

private class GenericMethodDelegateOwner {
    var calls = 0
    fun <T> select(
        first: T, p1: T, p2: T, p3: T, p4: T, p5: T, p6: T, p7: T, p8: T,
        p9: T, p10: T, p11: T, p12: T, p13: T, p14: T, p15: T, p16: T,
    ): T { calls++; return first }
}

class GenericMethodDelegateEntryTests {
    @TestAttribute
    fun boundGenericMethodRetainsReceiverAndReferenceArguments() {
        val owner = GenericMethodDelegateOwner()
        val callback: (String, String, String, String, String, String, String, String, String,
            String, String, String, String, String, String, String, String) -> String = owner::select
        check(callback("selected", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "") == "selected")
        check(callback("again", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "") == "again")
        check(owner.calls == 2)
    }
}
