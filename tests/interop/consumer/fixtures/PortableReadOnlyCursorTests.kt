package interoptests.portablereadonlycursors

import NUnit.Framework.TestAttribute
import ReadOnlyCursorInterop.GrowingReadOnlyFaces
import CollectionStorageInterop.RawAndGenericList

private fun expectNativeIndexerFailure(action: () -> Unit) {
    var rejected = false
    try { action() }
    catch (failure: IllegalStateException) {
        check(failure.message == "native indexer failure")
        rejected = true
    }
    check(rejected)
}

class PortableReadOnlyCursorInteropTests {
    @TestAttribute fun liveCursorsKeepExactClosedFacesInsteadOfRawStorage() {
        val source = GrowingReadOnlyFaces()
        val native: Any = source
        val numbers = native as List<Int>
        val texts = native as List<String?>
        val numeric = numbers.listIterator(1)
        val text = texts.listIterator(1)
        source.AddNumber(2)
        source.AddText(null)
        check(numeric.hasNext() && numeric.next() == 2 && !numeric.hasNext())
        check(text.hasNext() && text.next() == null && !text.hasNext())
        source.AddNumber(3)
        check(numeric.hasNext() && numeric.next() == 3 && !text.hasNext())
        check(text.previous() == null && text.previous() == "first")
        val emptyNumbers = numbers.listIterator()
        val emptyTexts = texts.listIterator()
        source.ClearNumbers()
        source.ClearTexts()
        check(!emptyNumbers.hasNext() && !emptyTexts.hasNext())
        check(source.Count == 3)
        source.AddNumber(4)
        val failing = numbers.listIterator()
        source.FailNextNumberRead()
        expectNativeIndexerFailure { failing.next() }
        check(failing.nextIndex() == 1)
        source.FailNextNumberRead()
        expectNativeIndexerFailure { failing.previous() }
        check(failing.nextIndex() == 0)
        check(failing.next() == 4 && !failing.hasNext())
    }

    @TestAttribute fun genericReadonlyFaceDoesNotEnumerateOrUseRawCount() {
        val source: Any = RawAndGenericList()
        val readonly = source as List<Int>
        val cursor = readonly.listIterator()
        check(cursor.next() == 7 && cursor.next() == 8 && !cursor.hasNext())
        check(cursor.previous() == 8 && cursor.previous() == 7 && !cursor.hasPrevious())
    }
}
