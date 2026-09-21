import NUnit.Framework.TestAttribute

private class LocalForeignSet : IterableClassifierStorage.SetAndDictionary()
private class LocalForeignInt : InheritedGenericOwners.IntBridge()
private class LocalForeignGeneric<T : Any> : InheritedGenericOwners.GenericBridge<T>() {
    fun forward(value: T): T = Echo(value)
}
private class LocalForeignNested : InheritedGenericOwners.NestedBridge()

private fun <T : Any> inheritedGenericEcho(value: LocalForeignGeneric<T>, item: T): T = value.Echo(item)

class InheritedGenericOwnerTests {
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
