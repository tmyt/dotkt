package roundtriptests.readonlycursors

import NUnit.Framework.TestAttribute
import roundtrip.readonlycursors.cursorInLibrary

class ReadOnlyListIterationTests {
    @TestAttribute fun genericDllCursorRetainsLiveNativeStorage() {
        val source = System.Collections.Generic.List<Int>()
        source.Add(1)
        val readonly = (source as Any) as List<Int>
        val cursor = cursorInLibrary(readonly, 1)
        source.Add(2)
        check(cursor.hasNext() && cursor.next() == 2 && cursor.previous() == 2)
        val strings = System.Collections.Generic.List<String?>()
        val texts = (strings as Any) as List<String?>
        val text = cursorInLibrary(texts, 0)
        strings.Add(null)
        check(text.hasNext() && text.next() == null)
    }
}
