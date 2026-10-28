package roundtrip.consumer

import NUnit.Framework.TestAttribute
import roundtrip.arrayviews.SharedArrayViews
import roundtrip.arrayviews.inlineViewOf
import roundtrip.arrayviews.intViewOf
import roundtrip.arrayviews.viewOf

class ImportedArrayListViewTests {
    @TestAttribute
    fun producerAndImportedInlineViewsKeepTheOriginalArrayAcrossDlls() {
        val values = arrayOf("first", "middle", "first")
        val ordinary = viewOf(values)
        var inlineCalls = 0
        val inlined = inlineViewOf(values) { inlineCalls++ }
        val sub = ordinary.subList(1, 3)
        val iterator = inlined.iterator()
        values[0] = "last"; values[2] = "last"
        check(ordinary[0] == "last" && sub[1] == "last" && iterator.next() == "last")
        val numbers = arrayOf(1, 2, 1)
        val view = inlineViewOf(numbers) { inlineCalls++ }
        check(inlineCalls == 2)
        numbers[0] = 3
        check(view[0] == 3)
        val primitive = intArrayOf(1, 2, 1)
        val primitiveView = intViewOf(primitive)
        primitive[0] = 3
        check(primitiveView[0] == 3)
        val owned = SharedArrayViews()
        val imported = owned.view
        owned.replace(7)
        check(imported[0] == 7 && imported[2] == 7)
        owned.values[0] = null
        check(imported[0] == null)
    }
}
