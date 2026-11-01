@file:OptIn(ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute
import PortableArrayCopyInterop.NativeArrays as CopyNativeArrays

@Suppress("UNCHECKED_CAST")
private fun <T> checkNativeReferenceCopy(source: Array<T>, destination: Array<T>) {
    var rejected = false
    try { source.copyInto(destination) } catch (_: Exception) { rejected = true }
    check(rejected)
}

class PortableArrayCopyInteropTests {
    @TestAttribute fun copiesPreserveNativeComponentsAndPrimitiveViewPolicy() {
        val strings = CopyNativeArrays.Text()
        check(CopyNativeArrays.Component(strings.copyOf()) == "System.String")
        check(CopyNativeArrays.Component(strings.copyOfRange(1, 2)) == "System.String")
        val grown = strings.copyOf(3)
        check(CopyNativeArrays.Component(grown) == "System.String" && grown[2] == null)
        check(CopyNativeArrays.Component(strings + "tail") == "System.String")
        check(CopyNativeArrays.Component(strings + listOf("tail")) == "System.String")
        check(CopyNativeArrays.Component(strings + arrayOf("tail")) == "System.String")
        val signed = CopyNativeArrays.Unsigned().asIntArray()
        check(CopyNativeArrays.Component(signed.copyOf()) == "System.Int32")
        check(CopyNativeArrays.Component(signed.copyOf(4)) == "System.Int32")
        check(CopyNativeArrays.Component(signed.copyOfRange(0, 2)) == "System.Int32")
        check(signed.copyOf().contentEquals(intArrayOf(-1, 0, 42)))
        @Suppress("UNCHECKED_CAST")
        val generic = signed as Array<Int>
        check(CopyNativeArrays.Component(generic.copyOf()) == "System.UInt32")
        check(CopyNativeArrays.Component(generic.copyOfRange(0, 2)) == "System.UInt32")
        check(CopyNativeArrays.Component(generic.copyOf(4)) == "System.Object")
        check(CopyNativeArrays.BoxedUInts(generic.copyOf(4)))
    }

    @TestAttribute fun nativeReferenceDowncastsUseCheckedElementStores() {
        val destination = CopyNativeArrays.Text()
        checkNativeReferenceCopy(arrayOf<Any>("changed", 42), destination as Array<Any>)
        check(destination.contentEquals(arrayOf("changed", "last")))
    }
}
