package interop.projectedlistmutationwitness

import MutableListInterop.DualMutableList
import NUnit.Framework.TestAttribute

private fun <T> mutateSelectedList(list: MutableList<T>, value: T, old: T) {
    val projected: MutableList<in T> = list
    val mutation = {
        check(projected.set(0, value) == old)
        check(projected.removeAt(0) == value)
    }
    mutation()
}

private fun mutateUnselectedList(list: MutableList<in String>) { list.set(0, "do not infer a view") }
private fun removeFromUnselectedList(list: MutableList<*>) { list.removeAt(0) }

class ProjectedListMutationWitnessTests {
    @TestAttribute fun projectedSetAndRemoveRetainTheExactObjectView() {
        val native = DualMutableList()
        val exact = (native as Any) as MutableList<Any?>
        val projected: MutableList<in Any?> = exact
        check(projected.set(0, null) == "a")
        check(projected.removeAt(0) == null)
        projected.add(1, "inserted")
        check(exact.size == 3 && exact[0] == "b" && exact[1] == "inserted" && exact[2] == "c")
        val integers = (native as Any) as MutableList<Int>
        check(native.Count == 2 && integers[0] == 7 && integers[1] == 9)
        check((projected as Any) === (native as Any))
    }

    @TestAttribute fun starRemovalRetainsTheExactValueView() {
        val native = DualMutableList()
        val exact = (native as Any) as MutableList<Int>
        val projected: MutableList<*> = exact
        check(projected.removeAt(0) == 7)
        check(exact.size == 1 && exact[0] == 9 && native.Count == 1)
        val objects = (native as Any) as MutableList<Any?>
        check(objects.size == 3 && objects[0] == "a")
    }

    @TestAttribute fun genericCallerAndClosureKeepTheSelectedMutationView() {
        val native = DualMutableList()
        val objects = (native as Any) as MutableList<Any?>
        mutateSelectedList(objects, null, "a")
        check(objects.size == 2 && objects[0] == "b" && native.Count == 2)
        val integers = (native as Any) as MutableList<Int>
        mutateSelectedList(integers, 42, 7)
        check(integers.size == 1 && integers[0] == 9 && objects.size == 2)
    }

    @TestAttribute fun absentWitnessStillRejectsBothAmbiguousMutations() {
        val native = DualMutableList()
        var failures = 0
        try { mutateUnselectedList((native as Any) as MutableList<in String>) }
        catch (_: IllegalStateException) { failures++ }
        try { removeFromUnselectedList((native as Any) as MutableList<*>) }
        catch (_: IllegalStateException) { failures++ }
        check(failures == 2 && native.Count == 2)
        val objects = (native as Any) as MutableList<Any?>
        check(objects.size == 3 && objects[0] == "a")
    }
}
