import NUnit.Framework.TestAttribute

class ComparableSignatureTests {
    @TestAttribute
    fun genericComparableStdlibCallsAndReferencesUseTheRuntimeSignature() {
        check("b".coerceAtLeast("c") == "c")
        check("b".coerceAtMost("a") == "a")
        check("b".coerceIn("a", "c") == "b")
        val floor: (String, String) -> String = String::coerceAtLeast
        val ceiling: (String, String) -> String = String::coerceAtMost
        check(floor("a", "b") == "b")
        check(ceiling("b", "a") == "a")
    }
}
