import NUnit.Framework.TestAttribute

private class LocalForeignSet : IterableClassifierStorage.SetAndDictionary()
private class LocalForeignInt : InheritedGenericOwners.IntBridge()
private class LocalForeignGeneric<T : Any> : InheritedGenericOwners.GenericBridge<T>() {
    fun forward(value: T): T = Echo(value)
}
private class LocalForeignNested : InheritedGenericOwners.NestedBridge()
private class LocalSwapped<A : Any, B : Any> : InheritedGenericOwners.SwappedBridge<A, B>() {
    fun first(value: B): B = First(value)
    fun second(value: A): A = Second(value)
    fun <R : Any> converted(first: B, second: A, result: R): R = Convert(first, second, result)
}
private class EnclosingInherited : InheritedGenericOwners.IntBridge() {
    fun nestedValue(): String = object : InheritedGenericOwners.GenericBridge<String>() {
        fun echo(): String = Echo("nested")
    }.echo()
}

private fun <T : Any> inheritedGenericEcho(value: LocalForeignGeneric<T>, item: T): T = value.Echo(item)

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
