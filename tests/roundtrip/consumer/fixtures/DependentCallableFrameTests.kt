import NUnit.Framework.TestAttribute
import roundtrip.dependentframes.Factory
import roundtrip.dependentframes.ConstructorCapture
import roundtrip.dependentframes.Item
import roundtrip.dependentframes.IntItem
import roundtrip.dependentframes.Owner
import roundtrip.dependentframes.Base
import roundtrip.dependentframes.Marker
import roundtrip.dependentframes.NullableItem
import roundtrip.dependentframes.constructFromMethod
import roundtrip.dependentframes.VariantOuter
import roundtrip.dependentframes.InnerBoundOwner
import roundtrip.dependentframes.constructInnerBoundOwner
import roundtrip.dependentnative.Marker as NativeMarker
import roundtrip.dependentnative.Item as NativeItem
import roundtrip.dependentnative.Outer as NativeOuter
import roundtrip.dependentnative.Owner as NativeOwner
import roundtrip.dependentnative.make as makeNativeOwner
import roundtrip.dependentnative.oneHop as forwardNativeOwner
import roundtrip.dependentnative.ForeignBoundBase
import RoundtripDependentKotlinBound.Owner as ForeignKotlinBoundOwner
import RoundtripDependentKotlinBound.Api as ForeignKotlinBoundApi

private fun <Tag, T> constructImportedOwner(value: T): Int where T : Base<T>, T : Marker<Tag> =
    Owner<Tag, T>().bound(value, value)

private class InnerHost : VariantOuter<String>() {
    inner class Sub : VariantOuter<String>.Inner()
}

private fun <Tag, T> constructImportedInnerOwner(): Int where T : VariantOuter<Tag>.Inner =
    InnerBoundOwner<Tag, T>().label()

private fun <Tag, T> constructImportedNativeOwner(value: T): Boolean where T : NativeMarker<Tag> =
    NativeOwner<Tag, T>(value).probe(value) && makeNativeOwner<Tag, T>(value, value) && forwardNativeOwner<Tag, T>(value)

private class KotlinBoundOwner<Tag, T> where T : ForeignBoundBase<Tag> {
    fun probe(): Boolean = ForeignKotlinBoundOwner<Tag, T>() != null && ForeignKotlinBoundApi.Probe<Tag, T>()
}
private fun <Tag, T> constructForeignKotlinBound(): Boolean where T : ForeignBoundBase<Tag> =
    KotlinBoundOwner<Tag, T>().probe()

private class KotlinMethodBoundOwner<Tag, T> where T : ForeignBoundBase<Tag> {
    fun probe(): Boolean = ForeignKotlinBoundApi.Probe<Tag, T>()
}
private fun <Tag, T> callForeignKotlinBound(): Boolean where T : ForeignBoundBase<Tag> =
    ForeignKotlinBoundApi.Probe<Tag, T>()
private fun <Tag, T> sourceBoundOnly(value: T): T where T : ForeignBoundBase<Tag> = value

class DependentCallableFrameTests {
    @TestAttribute fun foreignClrConstraintsRetainTheirExactKotlinBoundHead() {
        check(KotlinBoundOwner<String, ForeignBoundBase<String>>().probe())
        check(constructForeignKotlinBound<String, ForeignBoundBase<String>>())
        check(KotlinMethodBoundOwner<String, ForeignBoundBase<String>>().probe())
        check(callForeignKotlinBound<String, ForeignBoundBase<String>>())
        val source = ForeignBoundBase<String>()
        check(sourceBoundOnly<String, ForeignBoundBase<String>>(source) === source)
    }

    @TestAttribute fun importedNativeProofRowsPreserveWitnessAndStorageFrames() {
        check(constructImportedNativeOwner<String, NativeItem<String>>(NativeItem<String>()))
        val inner = NativeOuter<Int>().Item()
        check(constructImportedNativeOwner<Int, NativeOuter<Int>.Item>(inner))
    }

    @TestAttribute fun importedInnerBoundRetainsItsOuterVariance() {
        check(constructInnerBoundOwner<Any, InnerHost.Sub>() == 47)
        check(constructImportedInnerOwner<Any, InnerHost.Sub>() == 47)
    }

    @TestAttribute fun importedGenericMethodCanConstructItsDependentOwner() {
        constructFromMethod<String, Item>()
        constructFromMethod<Int, IntItem>()
        constructFromMethod<Int?, NullableItem>()
    }

    @TestAttribute fun genericConsumerCanConstructTheImportedDependentOwner() {
        check(constructImportedOwner<String, Item>(Item()) == 1)
        check(constructImportedOwner<Int, IntItem>(IntItem()) == 1)
        check(constructImportedOwner<Int?, NullableItem>(NullableItem()) == 1)
    }

    @TestAttribute fun importedDependentOwnerKeepsBoundAndUnboundTargets() {
        val owner = Owner<String, Item>()
        val first = Item()
        val second = Item()
        check(owner.unbound(first, first) == 1)
        check(owner.bound(first, second) == -2)
        check(owner.unbound(second, second) == 1)
        check(first.calls == 2 && second.calls == 1)
        val integerTagged = IntItem()
        check(Owner<Int, IntItem>().unbound(integerTagged, integerTagged) == 1)
    }

    @TestAttribute fun dependentOwnerCanBeConstructedFromAnotherGenericOwner() {
        Factory<String, Item>().constructOnly()
        Factory<Int, IntItem>().constructOnly()
        val value = Item()
        check(Owner<String, Item>().unbound(value, value) == 1)
    }

    @TestAttribute fun constructorCapturesKeepTheirPhysicalOwnerFrame() {
        check(ConstructorCapture<String, Item>().label.isNotEmpty())
        check(ConstructorCapture<Int, IntItem>().label.isNotEmpty())
    }
}
