package interop.setmembership

import NUnit.Framework.TestAttribute
import SetMembershipInterop.*
import SetArgumentInterop.ObjectMutableSetAndIntSet

private fun widen(value: Set<Any?>): Set<Any?> = value
private fun forward(value: Set<*>): Set<Any?> = widen(value)

class SetMembershipTests {
    @TestAttribute fun comparerSurvivesClosedStarAndWidenedMembership() {
        val native = ComparerSetAndList()
        check(native.NativeContains("A"))
        val source: Any = native
        @Suppress("UNCHECKED_CAST")
        val closed = source as Set<String>
        val star = source as Set<*>
        val wide = forward(star)
        check(closed.contains("A") && star.contains("A") && wide.contains("A"))
        check(closed.containsAll(listOf("A", "a")))
        check(wide.containsAll(listOf<Any?>("A", "a")))
        @Suppress("UNCHECKED_CAST")
        val concrete = source as HashSet<String>
        check(concrete.containsAll(listOf("A", "a")))
        check(!wide.contains("unrelated") && !wide.contains(42) && !wide.contains(null))
        check(wide.size == 1 && wide === source)
    }

    @TestAttribute fun mutableOnlySetUsesItsExactCollectionParentSlot() {
        val native = MutableOnlyComparerSet()
        check(native.NativeContains("A"))
        val source: Any = native
        val wide = forward(source as Set<*>)
        check(wide.contains("A") && wide.containsAll(listOf<Any?>("A", "a")))
        check(!wide.contains(42) && !wide.contains(null))
        val mutable = source as MutableSet<*>
        check(mutable.contains("A"))
        check(wide === source)
        val mixed: Any = ObjectMutableSetAndIntSet()
        val exact = mixed as MutableSet<Any?>
        check(exact.contains("a") && !exact.contains(7))
        @Suppress("UNCHECKED_CAST")
        val projected = mixed as MutableSet<in String>
        check(projected.contains("a"))
        val collection = source as MutableCollection<String>
        check(collection.contains("A") && collection.containsAll(listOf("A")))
    }

    @TestAttribute fun readonlySetInvokesItsInterfaceNotPublicLookalikeOrEnumerator() {
        val native = ReadonlyComparerSet()
        check(native.NativeContains("A"))
        val source: Any = native
        val star = source as Set<*>
        check(star.contains("A"))
        val wide = forward(star)
        check(wide.contains("A") && wide.containsAll(listOf<Any?>("A", "a")))
        check(!wide.contains("absent") && !wide.contains(42))
    }

    @TestAttribute fun nativeMembershipExceptionsRetainIdentityAndContainsAllShortCircuits() {
        val native = ThrowingReadonlyComparerSet()
        val source: Any = native
        val wide = forward(source as Set<*>)
        var caught = false
        try { wide.contains("boom") } catch (failure: System.InvalidOperationException) {
            check(failure === native.Failure); caught = true
        }
        check(caught)
        caught = false
        try { wide.containsAll(listOf<Any?>("A", "boom")) } catch (failure: System.InvalidOperationException) {
            check(failure === native.Failure); caught = true
        }
        check(caught)
        check(!wide.containsAll(listOf<Any?>("absent", "boom")))
    }

    @TestAttribute fun incompatibleCandidatesDoNotConvertToDefaultOrNumericValues() {
        val zero: Any = ZeroIntSet()
        val nonNull = forward(zero as Set<*>)
        check(nonNull.contains(0) && !nonNull.contains(null) && !nonNull.contains(0L))
        val nullable: Any = NullableIntSet()
        val view = forward(nullable as Set<*>)
        check(view.contains(null) && view.contains(2) && !view.contains(2L))
        check(view.containsAll(listOf<Any?>(null, 2)))
        val raw = System.Collections.ArrayList()
        raw.Add("value")
        val erased: Any = raw
        val list = erased as MutableList<*>
        check(list.contains("value") && !list.contains("absent"))
    }
}
