import NUnit.Framework.TestAttribute
import kotlin.clr.byref

private class LocalForeignSet : IterableClassifierStorage.SetAndDictionary()
private class LocalForeignInt : InheritedGenericOwners.IntBridge()
private class LocalForeignGeneric<T : Any> : InheritedGenericOwners.GenericBridge<T>() {
    fun forward(value: T): T = Echo(value)
}
private class LocalForeignNested : InheritedGenericOwners.NestedBridge()
private class LocalSwapped<A : Any, B : Any> : InheritedGenericOwners.SwappedBridge<A, B>() {
    fun first(value: B): B = First(value)
    fun second(value: A): A = Second(value)
    fun repeat(value: B): Array<B> = Repeat(value)
    fun nested(first: B, second: A): System.Tuple2<B, System.Tuple2<A, B>> = Nested(first, second)
    fun boxed(value: B): Any = First(value)
    fun same(value: B): Boolean = First(value) == value
    fun <R : Any> mixed(value: B, result: R): System.Tuple2<B, R> = Mix(value, result)
    fun <R : Any> converted(first: B, second: A, result: R): R = Convert(first, second, result)
}
private class EnclosingInherited : InheritedGenericOwners.IntBridge() {
    fun nestedValue(): String = object : InheritedGenericOwners.GenericBridge<String>() {
        fun echo(): String = Echo("nested")
    }.echo()
}

private fun <T : Any> inheritedGenericEcho(value: LocalForeignGeneric<T>, item: T): T = value.Echo(item)
private fun <T : Any> nativeRefCopy(box: InheritedGenericOwners.NativeReferenceBox<T>): T = box.Read()
private fun <T : Any> nativeRefReplace(box: InheritedGenericOwners.NativeReferenceBox<T>, value: T) {
    var live by byref(box.Read())
    live = value
}

class InheritedGenericOwnerTests {
    @TestAttribute fun inheritedOwnerArgumentsFollowTheirPermutation() {
        val foreign = InheritedGenericOwners.SwappedBridge<String, Int>()
        check(foreign.First(5) == 5)
        check(foreign.Second("foreign") == "foreign")
        val value = LocalSwapped<String, Int>()
        check(value.First(7) == 7)
        check(value.Second("direct") == "direct")
        check(value.first(9) == 9)
        check(value.second("self") == "self")
        val repeated = value.repeat(17)
        check(repeated.size == 2 && repeated[0] == 17 && repeated[1] == 17)
        val nested = value.nested(19, "nested")
        check(nested.Item1 == 19)
        check(nested.Item2.Item1 == "nested")
        check(nested.Item2.Item2 == 19)
        check(value.boxed(23) == 23 && value.same(29))
        val first = value::First
        val second = value::Second
        check(first(11) == 11)
        check(second("bound") == "bound")
        val unbound = LocalSwapped<String, Int>::First
        check(unbound(value, 13) == 13)
    }

    @TestAttribute fun openOwnerAndMethodArgumentsUseSeparateFrames() {
        val value = LocalSwapped<String, Int>()
        check(value.converted(7, "owner", true))
        check(value.converted(9, "owner", "method") == "method")
        val mixed = value.mixed(31, "method")
        check(mixed.Item1 == 31 && mixed.Item2 == "method")
    }

    @TestAttribute fun anonymousReceiverDoesNotUseItsEnclosingBaseArguments() {
        val value = EnclosingInherited()
        check(value.Echo(7) == 7)
        check(value.nestedValue() == "nested")
    }

    @TestAttribute fun classSlotWinsOverImplementedInterfaceSlots() {
        val value = LocalForeignSet()
        check(value.Add(7))
        check(!value.Add(7))
        check(value.Contains(7))
    }

    @TestAttribute fun inheritedClosedOwnerAndMethodFramesRemainDistinct() {
        val value = LocalForeignInt()
        check(value.Echo(7) == 7)
        check(value.Convert(9, "result") == "result")
    }

    @TestAttribute fun inheritedOpenOwnerUsesItsCallerFrame() {
        val strings = LocalForeignGeneric<String>()
        val integers = LocalForeignGeneric<Int>()
        check(inheritedGenericEcho(strings, "value") == "value")
        check(inheritedGenericEcho(integers, 9) == 9)
        check(strings.forward("self") == "self")
        check(integers.forward(11) == 11)
        val stringRef = InheritedGenericOwners.NativeReferenceBox("before")
        val stringCopy = nativeRefCopy(stringRef)
        nativeRefReplace(stringRef, "after")
        check(stringCopy == "before" && stringRef.Read() == "after")
        val intRef = InheritedGenericOwners.NativeReferenceBox(37)
        val intCopy = nativeRefCopy(intRef)
        nativeRefReplace(intRef, 41)
        check(intCopy == 37 && intRef.Read() == 41)
    }

    @TestAttribute fun inheritedNestedOwnerKeepsBothTypeArguments() {
        val value = LocalForeignNested()
        check(value.Outer("outer") == "outer")
        check(value.Inner(9) == 9)
    }

    @TestAttribute fun boundReferencesKeepTheirInheritedOwner() {
        val integer = LocalForeignInt()
        val echo = integer::Echo
        check(echo(11) == 11)
        val generic = LocalForeignGeneric<String>()
        val genericEcho = generic::Echo
        check(genericEcho("bound") == "bound")
    }
}
