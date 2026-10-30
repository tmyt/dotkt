package roundtriptests.portabletext

import NUnit.Framework.TestAttribute
import roundtrip.portabletext.*

class PortableTextTests {
    @TestAttribute fun importedBodiesAndSavedReferencesPreserveTextOperations() {
        check(replaceText("ab", "", "_", false) == "_a_b_")
        check(replaceText("oneONE", "one", "x", true) == "xx")
        check(savedReplacement()("ababa", "ba", "!", false) == "a!!")
        check(replaceFirstChar("\u212AKk", 'k', '!') == "!Kk")
        check(repeatedText("xy", 3) == "xyxyxy")
        check(capitalizedText("éclair") == "Éclair")
        check(controlCharacter(0x1f.toChar()) && controlCharacter(0x9f.toChar()))
        check(!controlCharacter(0x20.toChar()) && !controlCharacter(0xd800.toChar()))
    }

    @TestAttribute fun importedInlineReplacementEvaluatesCallbackOnce() {
        var calls = 0
        val suffix = "!"
        check(observeReplacement("aaaa", "aa", "x") { calls++; it + suffix } == "xx!")
        check(calls == 1)
        check(observeReplacement("ab", "", "_") { calls++; it } == "_a_b_")
        check(calls == 2)
        check(observeReplacement("abc", "not found", "_") { calls++; it } == "abc")
        check(calls == 3)
    }
}
