import NUnit.Framework.TestAttribute
import NativeGenericNullableSlots.*

private class NativeGenericIntSource(override val Item: Int?) : Source<Int?>
private class NativeGenericLongSource(override val Item: Long?) : DerivedSource<Long?>
private class NativeGenericTextSource(override val Item: String?) : Source<String?>
private class NativeGenericFixedSource(override val Item: Long?) : FixedSource
private class NativeGenericMutableSource(override var Item: Long?) : MutableSource<Long?>
private class NativeGenericBaseSource(override val Item: Int?) : BaseSource<Int?>()
private fun <T> readNativeGenericSource(value: Source<T>): T = value.Item

class NativeGenericNullableSlotTests {
    @TestAttribute fun interfaceGetterClosesInItsErasedApplicationFrame() {
        val present = NativeGenericIntSource(42)
        val absent = NativeGenericIntSource(null)
        val zero = NativeGenericIntSource(0)
        check(present.Item == 42 && absent.Item == null && zero.Item == 0)
        val slot: Source<Int?> = present
        val empty: Source<Int?> = absent
        check(slot.Item == 42 && empty.Item == null)
        check(readNativeGenericSource(slot) == 42 && readNativeGenericSource(empty) == null)
        check(Api.ReadBoxed(present) == 42 && Api.ReadBoxed(absent) == null && Api.ReadBoxed(zero) == 0)

        val wide: Source<Long?> = NativeGenericLongSource(5_000_000_000L)
        val noWide: Source<Long?> = NativeGenericLongSource(null)
        check(readNativeGenericSource(wide) == 5_000_000_000L && readNativeGenericSource(noWide) == null)
        check(Api.ReadBoxed(wide) == 5_000_000_000L && Api.ReadBoxed(noWide) == null)

        val text: Source<String?> = NativeGenericTextSource("value")
        val noText: Source<String?> = NativeGenericTextSource(null)
        check(readNativeGenericSource(text) == "value" && readNativeGenericSource(noText) == null)
        check(Api.ReadText(text) == "value" && Api.ReadText(noText) == null)

        val fixed: FixedSource = NativeGenericFixedSource(5_000_000_000L)
        val noFixed: FixedSource = NativeGenericFixedSource(null)
        check(fixed.Item == 5_000_000_000L && noFixed.Item == null)
        check(Api.ReadFixed(fixed) == 5_000_000_000L && Api.ReadFixed(noFixed) == null)
    }

    @TestAttribute fun mutablePropertyPreservesItsTypedScalarAndNativeSlot() {
        val value = NativeGenericMutableSource(null)
        val slot: MutableSource<Long?> = value
        check(value.Item == null && slot.Item == null && Api.ReadMutable(value) == null)
        slot.Item = 5_000_000_000L
        check(value.Item == 5_000_000_000L && Api.ReadMutable(value) == 5_000_000_000L)
        Api.WriteMutable(value, 6_000_000_000L)
        check(value.Item == 6_000_000_000L && slot.Item == 6_000_000_000L)
        Api.WriteMutable(value, null)
        check(value.Item == null && slot.Item == null)
        value.Item = 0L
        check(slot.Item == 0L && Api.ReadMutable(value) == 0L)
    }

    @TestAttribute fun abstractBaseGetterClosesInItsErasedApplicationFrame() {
        val present = NativeGenericBaseSource(23)
        val absent = NativeGenericBaseSource(null)
        val slot: BaseSource<Int?> = present
        val empty: BaseSource<Int?> = absent
        check(present.Item == 23 && absent.Item == null)
        check(slot.Item == 23 && empty.Item == null)
        check(Api.ReadBase(present) == 23 && Api.ReadBase(absent) == null)
    }
}
