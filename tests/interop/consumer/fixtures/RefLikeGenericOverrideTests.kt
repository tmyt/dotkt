import ByRefLikeInterop.ByRefLikeApi
import ByRefLikeInterop.IRelayValue
import ByRefLikeInterop.IValueRelay
import NUnit.Framework.TestAttribute

private open class KotlinValueRelay : IValueRelay {
    override fun <T : IRelayValue> Read(value: T): Int = value.Read()
    override fun <T : IRelayValue> Read(value: T, offset: Int): Int = value.Read() + offset
}

private class DerivedValueRelay : KotlinValueRelay() {
    override fun <T : IRelayValue> Read(value: T): Int = super.Read(value) + 1
}

class RefLikeGenericOverrideTests {
    @TestAttribute fun preservesTheSelectedNativeGenericSlot() {
        check(ByRefLikeApi.ReadStack(KotlinValueRelay()) == 11)
        check(ByRefLikeApi.ReadStack(DerivedValueRelay()) == 12)
        check(ByRefLikeApi.ReadHeap(DerivedValueRelay()) == 10)
    }
}
