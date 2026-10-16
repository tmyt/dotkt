import NUnit.Framework.TestAttribute
import CollectionStorageInterop.ForeignListFaces
import CollectionStorageInterop.ReadOnlyListDictionary
import CollectionStorageInterop.MutableListDictionary

private fun foreignReadOnlyCount(value: Any): Int = (value as List<*>).size
private fun foreignMutableCount(value: Any): Int = (value as MutableList<*>).size
private fun foreignReadOnlyGet(value: Any): Any? = (value as List<*>)[0]
private fun foreignMutableGet(value: Any): Any? = (value as MutableList<*>)[0]
private fun foreignClosedRead(value: List<Any?>): Any? = value[0]
private fun foreignForwardedRead(value: List<*>): Any? = foreignClosedRead(value)
private fun checkForeignListAmbiguous(operation: () -> Any?) {
    var rejected = false
    try { operation() } catch (_: IllegalStateException) { rejected = true }
    check(rejected)
}

class ForeignListFacesTests {
    @TestAttribute fun rawListCountAndGetUseItsAvailableFace() {
        val value = ForeignListFaces.Raw()
        check(foreignReadOnlyCount(value) == 2)
        check(foreignMutableCount(value) == 2)
        check(foreignReadOnlyGet(value) == 7)
        check(foreignMutableGet(value) == 7)
    }

    @TestAttribute fun genericOnlyListCountAndGetUseItsAvailableFace() {
        val value = ForeignListFaces.Generic()
        check(foreignReadOnlyCount(value) == 2)
        check(foreignMutableCount(value) == 2)
        check(foreignReadOnlyGet(value) == 7)
        check(foreignMutableGet(value) == 7)
    }

    @TestAttribute fun readOnlyOnlyListDoesNotGainMutableIdentity() {
        val value = ForeignListFaces.ReadOnly()
        check(value is List<*> && value !is MutableList<*>)
        check(value as? MutableList<*> == null)
        check(foreignReadOnlyCount(value) == 2 && foreignReadOnlyGet(value) == 7)
        var rejected = false
        try { value as MutableList<*> } catch (_: ClassCastException) { rejected = true }
        check(rejected)
    }

    @TestAttribute fun foreignCastsPreserveIdentity() {
        for (value in arrayOf(ForeignListFaces.Raw(), ForeignListFaces.Generic(), ForeignListFaces.ReadOnly())) {
            check(value is List<*>)
            check(value as List<*> === value)
            check(value as? List<*> === value)
        }
        for (value in arrayOf(ForeignListFaces.Raw(), ForeignListFaces.Generic())) {
            check(value is MutableList<*>)
            check(value as MutableList<*> === value)
            check(value as? MutableList<*> === value)
        }
    }

    @TestAttribute fun foreignSmartCastMembersAndContains() {
        for (value in arrayOf(ForeignListFaces.Raw(), ForeignListFaces.Generic(), ForeignListFaces.ReadOnly())) {
            check(value is List<*>)
            check(value.size == 2 && value[1] == 9)
            check(value.contains(7) && !value.contains(11))
            check(value.iterator().next() == 7)
        }
    }

    @TestAttribute fun listCountSelectsListRatherThanAnIndependentDictionary() {
        for (value in arrayOf<Any>(ReadOnlyListDictionary(), MutableListDictionary())) {
            check(foreignReadOnlyCount(value) == 1)
            check(foreignReadOnlyGet(value) == "list")
        }
    }

    @TestAttribute fun closedViewsSelectTheirPreferenceAndStarWideningDropsIt() {
        val value = ForeignListFaces.ExactViews()
        check((value as List<Any>)[0] == "object-view")
        check((value as List<Any>).size == 1)
        check((value as List<String>)[0] == "string-view")
        check((value as List<String>).size == 2)
        val closed = value as List<String>
        check(!closed.isEmpty())
        check(closed.contains("string-view") && !closed.contains("object-view"))
        check(closed.containsAll(listOf("string-view", "second")))
        check(closed.iterator().next() == "string-view")
        var joined = ""
        for (element in closed) joined += element
        check(joined == "string-viewsecond")
        check(closed.indexOf("second") == 1 && closed.lastIndexOf("second") == 1)
        check(closed.listIterator().next() == "string-view")
        check(closed.listIterator(1).next() == "second")
        check(closed.subList(0, 2)[1] == "second")
        val stringView: List<*> = value as List<String>
        checkForeignListAmbiguous { stringView[0] }
        checkForeignListAmbiguous { stringView.size }
        checkForeignListAmbiguous { stringView.isEmpty() }
        checkForeignListAmbiguous { stringView.contains("object-view") }
        checkForeignListAmbiguous { stringView.containsAll(listOf("object-view")) }
        checkForeignListAmbiguous { stringView.iterator().next() }
        checkForeignListAmbiguous { stringView.indexOf("object-view") }
        checkForeignListAmbiguous { stringView.lastIndexOf("object-view") }
        checkForeignListAmbiguous { stringView.listIterator() }
        checkForeignListAmbiguous { stringView.listIterator(1) }
        checkForeignListAmbiguous { stringView.subList(0, 1) }
        val objectView: List<*> = value as List<Any>
        checkForeignListAmbiguous { objectView[0] }
        checkForeignListAmbiguous { objectView.size }
        check(foreignClosedRead(value as List<Any>) == "object-view")
        check(foreignForwardedRead(stringView) == "object-view")
        check(foreignForwardedRead(objectView) == "object-view")
        for (single in arrayOf(ForeignListFaces.Raw(), ForeignListFaces.Generic())) {
            val widened = single as List<Any?>
            check(widened.size == 2 && widened[0] == 7 && !widened.isEmpty())
            check(widened.contains(7) && !widened.contains("wrong"))
            check(widened.containsAll(listOf(7, 9)))
            check(widened.iterator().next() == 7 && widened.indexOf(9) == 1 && widened.lastIndexOf(9) == 1)
            check(widened.listIterator().next() == 7 && widened.listIterator(1).next() == 9)
            check(widened.subList(0, 2)[1] == 9)
        }
        val mutable = ForeignListFaces.Generic() as MutableList<Int>
        val slice = (mutable as List<Any?>).subList(0, 2)
        mutable[1] = 11
        check(slice[1] == 11)
    }
}
