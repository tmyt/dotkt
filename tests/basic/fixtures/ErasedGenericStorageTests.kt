import NUnit.Framework.TestAttribute

private class ErasedMutableStorage<T>(var value: T) {
    fun read(): T = value
    fun write(next: T) { value = next }
    fun echo(next: T): T = next
}

@Suppress("UNCHECKED_CAST")
private fun <T> erasedStorageView(value: Any): ErasedMutableStorage<T> =
    value as ErasedMutableStorage<T>

private class ErasedInheritedPair<A, B>(val first: A, val second: B)
private interface ErasedInheritedSlot<T> { fun pass(value: T): T }
private open class ErasedInheritedBase<T> { fun pass(value: T): T = value }
private class ErasedInheritedDerived<A, B> :
    ErasedInheritedBase<ErasedInheritedPair<A, B>>(), ErasedInheritedSlot<ErasedInheritedPair<A, B>>

class ErasedGenericStorageTests {
    @TestAttribute
    fun inheritedNonvirtualMethodFillsConstructedInterfaceSlot() {
        val value = ErasedInheritedPair(42, "nested")
        val receiver: ErasedInheritedSlot<ErasedInheritedPair<Int, String>> = ErasedInheritedDerived()
        val result = receiver.pass(value)
        check(result === value)
        check(result.first == 42)
        check(result.second == "nested")
    }

    @TestAttribute
    fun uncheckedViewWritesTheSamePropertyStorage() {
        val strings = ErasedMutableStorage("initial")
        val erased = erasedStorageView<Any?>(strings)
        erased.value = 42
        val integers = erasedStorageView<Int>(erased)
        check(integers.value == 42)
        erased.value = null
        check(erased.value == null)
        erased.value = "restored"
        check(strings.value == "restored")
        check(integers === strings)
    }

    @TestAttribute
    fun uncheckedViewWritesThroughGenericMethods() {
        val strings = ErasedMutableStorage("initial")
        val erased = erasedStorageView<Any?>(strings)
        erased.write(42)
        val integers = erasedStorageView<Int>(erased)
        check(integers.read() == 42)
        erased.write(null)
        check(erased.read() == null)
        erased.write("restored")
        check(strings.read() == "restored")
        check(integers === strings)
    }

    @TestAttribute
    fun genericParameterDoesNotCheckTheConstructionArgument() {
        val strings = ErasedMutableStorage("initial")
        val erased = erasedStorageView<Any?>(strings)
        check(erased.echo(42) == 42)
        check(erased.echo(null) == null)
        val token = Any()
        check(erased.echo(token) === token)
        check(strings.value == "initial")
    }
}
