import NUnit.Framework.TestAttribute
import NativeNullableSlots.*

private class NativeNullableSource(override val Item: Int?) : ISource
private class NativeMutableSource(override var Item: Long?) : IMutableSource
private class NativeBaseSource(override val Item: Int?) : BaseSource()
private class NativeTransform : ITransform {
    override fun Transform(value: Int?): Int? = value?.plus(1)
}

class NativeNullableSlotTests {
    @TestAttribute fun interfaceGetterKeepsNativeNullableReturn() {
        val present: ISource = NativeNullableSource(42)
        val absent: ISource = NativeNullableSource(null)
        check(present.Item == 42 && absent.Item == null)
        check(Api.Read(present) == 42 && Api.Read(absent) == null)
    }

    @TestAttribute fun interfaceSetterKeepsNativeNullableParameter() {
        val value: IMutableSource = NativeMutableSource(null)
        check(value.Item == null && Api.ReadMutable(value) == null)
        value.Item = 5_000_000_000L
        check(Api.ReadMutable(value) == 5_000_000_000L)
        Api.Write(value, 6_000_000_000L)
        check(value.Item == 6_000_000_000L)
        Api.Write(value, null)
        check(value.Item == null)
    }

    @TestAttribute fun baseGetterAndMethodKeepNativeNullableSlots() {
        val present: BaseSource = NativeBaseSource(23)
        val absent: BaseSource = NativeBaseSource(null)
        check(present.Item == 23 && absent.Item == null)
        check(Api.ReadBase(present) == 23 && Api.ReadBase(absent) == null)
        val transform: ITransform = NativeTransform()
        check(transform.Transform(41) == 42 && transform.Transform(null) == null)
        check(Api.Apply(transform, 41) == 42 && Api.Apply(transform, null) == null)
    }
}
