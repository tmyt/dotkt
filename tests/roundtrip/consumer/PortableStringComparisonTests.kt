package roundtriptests.portablestringcomparison

import NUnit.Framework.TestAttribute
import roundtrip.portablestringcomparison.*

class PortableStringComparisonTests {
    @TestAttribute fun libraryMethodsReferencesAndComparatorUseTheSameRules() {
        check(equalText(null, null, true) && equalText("KΣ", "kς", true))
        check(!equalText("K", "k", false) && compareText("K", "k", true) == 0)
        check(textPrefix("xKΣ", "kσ", 1) && textSuffix("xKΣ", "kς"))
        check(!textPrefix("xK", "k", Int.MAX_VALUE))
        val compare = savedTextComparison()
        val equals = savedTextEquality()
        check(compare("K", "k", true) == 0 && equals("K", "k", true))
        check(textOrder().compare("K", "k") == 0)
    }

    @TestAttribute fun importedInlineComparisonPreservesResultAndSingleEvaluation() {
        var calls = 0
        check(observedTextComparison("K", "k") { calls++; it } == 0)
        check(observedTextComparison("a", "z") { calls++; if (it < 0) 42 else 0 } == 42)
        check(calls == 2)
    }
}
