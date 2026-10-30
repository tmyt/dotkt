package roundtriptests.textbounds

import NUnit.Framework.TestAttribute
import roundtrip.textbounds.*

private fun importedBoundsKind(action: () -> Unit): Int {
    try { action() }
    catch (_: IllegalArgumentException) { return 2 }
    catch (_: IndexOutOfBoundsException) { return 1 }
    return 0
}

class TextBoundsTests {
    @TestAttribute fun librarySlicesCopiesAndReferencesKeepValidationAndIdentity() {
        val chars = charArrayOf('a', 'b', 'c')
        check(charSlice(chars, 1, 3) == "bc" && textSlice("abc", 1, 3).concatToString() == "bc")
        val destination = charArrayOf('#', '#', '#')
        check(copyText("abc", destination, 0, 1, 3) === destination)
        check(savedCopy()("abc", destination, 0, 0, 3) === destination)
        check(destination.concatToString() == "abc")
        check(decodeText(encodeText("abc", 1, 3), 0, 2) == "bc")
        check(importedBoundsKind { charSlice(chars, 2, 1) } == 2)
        check(importedBoundsKind { textSlice("abc", -1, 1) } == 1)
        check(importedBoundsKind { copyText("abc", destination, Int.MAX_VALUE, 0, 1) } == 1)
    }

    @TestAttribute fun importedInlineCopyEvaluatesCallbackOnlyAfterValidCopy() {
        val destination = charArrayOf('#', '#', '#')
        var calls = 0
        check(observedCopy("abc", destination, 0, 2) { calls++; it } === destination)
        check(destination.concatToString() == "ab#" && calls == 1)
        check(importedBoundsKind { observedCopy("abc", destination, Int.MIN_VALUE, Int.MAX_VALUE) { calls++; it } } == 1)
        check(calls == 1 && destination.concatToString() == "ab#")
    }
}
