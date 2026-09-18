import NUnit.Framework.TestAttribute
import CollectionStorageInterop.ForeignListFaces
import CollectionStorageInterop.ReadOnlyListDictionary
import CollectionStorageInterop.MutableListDictionary

private fun foreignReadOnlyCount(value: Any): Int = (value as List<*>).size
private fun foreignMutableCount(value: Any): Int = (value as MutableList<*>).size
private fun foreignReadOnlyGet(value: Any): Any? = (value as List<*>)[0]
private fun foreignMutableGet(value: Any): Any? = (value as MutableList<*>)[0]

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

    @TestAttribute fun concreteAndProjectedViewsKeepTheirSelectedInterface() {
        val value = ForeignListFaces.ExactViews()
        check((value as List<Any>)[0] == "object-view")
        check((value as List<Any>).size == 1)
        val stringView: List<*> = value as List<String>
        check(stringView[0] == "string-view" && stringView.size == 2)
        val objectView: List<*> = value as List<Any>
        check(objectView[0] == "object-view" && objectView.size == 1)
    }
}
