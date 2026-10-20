package interop.collectionhash

import NUnit.Framework.TestAttribute
import HashInterop.*

class CollectionHashInteropTests {
    @TestAttribute
    fun nativeObjectHashesRemainNativeWhileKotlinCallsAreStructural() {
        val first = HashOracle.MakeList()
        val second = HashOracle.MakeList()
        val left: Any = first
        val right: Any = second
        check(left.hashCode() == right.hashCode())
        check(HashOracle.NativeHash(first) == HashOracle.IdentityHash(first))
        val stored = hashMapOf(left to "stored")
        check(HashOracle.HasKey(stored, second))
    }

    @TestAttribute
    fun explicitNativeConstructorsKeepTheirDefaultOrSelectedComparer() {
        val first = HashOracle.MakeList()
        val second = HashOracle.MakeList()
        val native = System.Collections.Generic.Dictionary<System.Collections.Generic.List<Int>, String>()
        native[first] = "native"
        check(HashOracle.HasKey(native, first))
        check(!HashOracle.HasKey(native, second))
        val insensitive = System.Collections.Generic.Dictionary<String, Int>(HashOracle.IgnoreCaseComparer())
        insensitive["KEY"] = 1
        check(insensitive.ContainsKey("key"))
    }

    @TestAttribute
    fun nativeHashOverridesWinButNewSlotHidingDoesNotReplaceTheObjectSlot() {
        val value = HashOnlyList()
        val opaque: Any = value
        check(opaque.hashCode() == 211 && value.Calls == 1)
        val hidden: Any = HiddenHashList()
        check(hidden.hashCode() == listOf(23, 42).hashCode())
    }
}
