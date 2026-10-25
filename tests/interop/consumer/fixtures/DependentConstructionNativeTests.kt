package interop.dependentconstruction

import DependentConstructionInterop.IMarker
import DependentConstructionInterop.NativeNumber
import DependentConstructionInterop.NativeOwner
import DependentConstructionInterop.EnumerableOwner
import System.Collections.Generic.IEnumerable
import NUnit.Framework.TestAttribute

private class ReferenceItem : IMarker<String>

private fun <Tag, T> constructNative(value: T): T where T : IMarker<Tag> =
    NativeOwner<Tag, T>().Echo(value)

private interface KotlinMarker<Tag> : IMarker<Tag>
private class KotlinItem<Tag> : KotlinMarker<Tag>
private class KotlinOwner<Tag, T> where T : KotlinMarker<Tag> {
    fun probe(): Boolean = NativeOwner<Tag, T>() != null
}
private fun <Tag, T> constructKotlinOwner(value: T, expected: Any): Boolean where T : KotlinMarker<Tag> =
    KotlinOwner<Tag, T>().probe() && value === expected
private class NativeOuter<Tag> { inner class Item : KotlinMarker<Tag> }
private class StoredKotlinOwner<Tag, T>(private val value: T, private val optional: T?) where T : KotlinMarker<Tag> {
    fun probe(expected: Any): Boolean = NativeOwner<Tag, T>() != null && value === expected && optional == null
}
private fun <Tag, T> constructStoredOwner(value: T): Boolean where T : KotlinMarker<Tag> =
    StoredKotlinOwner<Tag, T>(value, null).probe(value)
private fun <Tag, T> forwardConstruction(value: T): Boolean where T : KotlinMarker<Tag> =
    constructStoredOwner<Tag, T>(value)

private interface KotlinEnumerable<Tag> : IEnumerable<Tag>
private class EnumerableKotlinOwner<Tag, T> where T : KotlinEnumerable<Tag> {
    fun probe(): Boolean = EnumerableOwner<Tag, T>() != null
}
private fun <Tag, T> constructEnumerableOwner(): Boolean where T : KotlinEnumerable<Tag> =
    EnumerableKotlinOwner<Tag, T>().probe()

private fun <Tag, T> same(value: T, expected: Any): Boolean where T : KotlinMarker<Tag> = value === expected
private class ErasedOwner<Tag> {
    fun <T> matches(value: T, expected: Any): Boolean where T : KotlinMarker<Tag> = same<Tag, T>(value, expected)
}

class DependentConstructionNativeTests {
    @TestAttribute fun exactNativeConstraintsPermitReferenceAndStructArguments() {
        val reference = ReferenceItem()
        check(constructNative<String, ReferenceItem>(reference) === reference)
        check(constructNative<Int, NativeNumber>(NativeNumber(23)).Number == 23)
    }

    @TestAttribute fun kotlinBoundsPreserveRequiredTransitiveNativeProofs() {
        val reference = KotlinItem<String>()
        check(KotlinOwner<String, KotlinItem<String>>().probe())
        check(constructKotlinOwner<String, KotlinItem<String>>(reference, reference))
        check(constructStoredOwner<String, KotlinItem<String>>(reference))
        check(forwardConstruction<String, KotlinItem<String>>(reference))
        val inner = NativeOuter<Int>().Item()
        check(constructKotlinOwner<Int, NativeOuter<Int>.Item>(inner, inner))
        check(constructStoredOwner<Int, NativeOuter<Int>.Item>(inner))
        check(forwardConstruction<Int, NativeOuter<Int>.Item>(inner))
        check(EnumerableKotlinOwner<String, KotlinEnumerable<String>>().probe())
        check(constructEnumerableOwner<String, KotlinEnumerable<String>>())
    }

    @TestAttribute fun pureKotlinForwardingDoesNotAcquireInheritedNativeConstraints() {
        val original = KotlinItem<Int>()
        @Suppress("UNCHECKED_CAST")
        val viewed = original as KotlinMarker<String>
        @Suppress("UNCHECKED_CAST")
        val owner = ErasedOwner<Int>() as ErasedOwner<String>
        check(owner.matches<KotlinMarker<String>>(viewed, original))
    }
}
