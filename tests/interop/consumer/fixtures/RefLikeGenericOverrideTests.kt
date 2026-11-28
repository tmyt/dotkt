import ByRefLikeInterop.ByRefLikeApi
import ByRefLikeInterop.IRelayValue
import ByRefLikeInterop.IValueRelay
import ByRefLikeInterop.IStackFactory
import ByRefLikeInterop.KeyedRelay
import ByRefLikeInterop.RelayResult
import ByRefLikeInterop.SpecificRelayResult
import NUnit.Framework.TestAttribute

private open class KotlinValueRelay : IValueRelay {
    override fun <T : IRelayValue> Read(value: T): Int = value.Read()
    override fun <T : IRelayValue> Read(value: T, offset: Int): Int = value.Read() + offset
}

private class DerivedValueRelay : KotlinValueRelay() {
    override fun <T : IRelayValue> Read(value: T): Int = super.Read(value) + 1
}

private interface KotlinFactory { fun <T> Make(): RelayResult }
private class MixedFactory : KotlinFactory, IStackFactory {
    override fun <T> Make(): SpecificRelayResult = SpecificRelayResult().also { it.Tag = 42 }
}

private class IntKeyRelay : KeyedRelay<Int>() {
    override fun <T : IRelayValue> Read(value: T, key: Int): Int = value.Read() + key
    override fun <T : IRelayValue> Read(value: T, key: String): Int = value.Read() + key.length
}

class RefLikeGenericOverrideTests {
    @TestAttribute fun preservesTheSelectedNativeGenericSlot() {
        check(ByRefLikeApi.ReadStack(KotlinValueRelay()) == 11)
        check(ByRefLikeApi.ReadStack(DerivedValueRelay()) == 12)
        check(ByRefLikeApi.ReadHeap(DerivedValueRelay()) == 10)
        check(ByRefLikeApi.ReadKeyed(IntKeyRelay()) == 23)
    }

    @TestAttribute fun nativeAdmissionDoesNotHideKotlinCovariantOverride() {
        val mixed = MixedFactory()
        val face: KotlinFactory = mixed
        check(face.Make<String>().Tag == 42)
        check(ByRefLikeApi.MakeStack(mixed).Tag == 42)
    }
}
