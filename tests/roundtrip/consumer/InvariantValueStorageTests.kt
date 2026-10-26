import NUnit.Framework.TestAttribute
import GenericValueInterop.NativeBoxApi
import GenericValueInterop.NativeBoxContainer
import GenericValueInterop.NativeBoxContainerFactory
import GenericValueInterop.NativeBoxCreator
import GenericValueInterop.NativeBoxFactory
import GenericValueInterop.NativeBoxConsumer
import kotlin.clr.byref
import roundtrip.genericvalueinterop.Box
import roundtrip.invariantstorage.*

private fun forwardedNativeBox(): Box<String> = Box("forwarded")
private class LocalStoredBox<T>(val value: T)
private class ForwardedNativeBoxCreator : NativeBoxCreator {
    override fun Create(): Box<String> = forwardedNativeBox()
}

class InvariantValueStorageTests {
    @TestAttribute
    fun constructorsAndFieldsRetainUncheckedIdentityAcrossDlls() {
        val value = storedView<Int>()
        val holder = StoredHolder<Int>(value)
        check(holder.view === value && holder.view === UniversalStoredView)
        check(holder.view.code(42) == 7)
        val field = StoredReferenceField()
        check(field.view === UniversalStoredView && field.view.code("text") == 7)
    }

    @TestAttribute
    fun topFieldsAndLambdaResultsRetainUncheckedIdentityAcrossDlls() {
        check(storedGlobal === UniversalStoredView && storedGlobal.code(42) == 7)
        val supplied = storedSupplier()()
        check(supplied === UniversalStoredView && supplied.code(42) == 7)
    }

    @TestAttribute
    fun sourceContainerElementsRetainUncheckedIdentityAcrossDlls() {
        val elements = storedList()
        check(elements[0] === UniversalStoredView && elements[0].code(42) == 7)
        val pair = storedPair()
        check(pair.first === UniversalStoredView && pair.first.code(42) == 7 && pair.second == 1)
    }

    @TestAttribute
    fun projectedManagedReferenceSlotsRetainTheirCarrierPointee() {
        var slot: Box<*> = Box(1)
        val replacement: Box<*> = Box("after")
        replaceProjectedSlot(byref(slot), replacement)
        check(slot === replacement && slot.value == "after")
        val holder = ProjectedReferenceField()
        holder.replace(replacement)
        check(holder.value === replacement && holder.value.value == "after")
    }

    @TestAttribute
    fun sourceReferenceAndNativeArgumentRolesRetainVarianceAndAliases() {
        var variant: StoredVariant<Any> = StoredVariant("before")
        val replacement = StoredVariant(42)
        replaceVariant(byref(variant), replacement)
        check(variant === replacement && variant.value == 42)
        var list: List<Any> = listOf("before")
        val newList = listOf(42)
        replaceSourceList(byref(list), newList)
        check(list === newList && list[0] == 42)
        var array = arrayOf(StoredVariant("before"))
        val newArray = arrayOf(StoredVariant("after"))
        replaceVariantArray(byref(array), newArray)
        check(array === newArray && array[0].value == "after")
        val native = System.Collections.Generic.List<StoredVariant<Any>>()
        native.Add(replacement)
        check(native[0] === replacement)
        val nested = System.Collections.Generic.List<List<Any>>()
        nested.Add(newList)
        check(nested[0] === newList)
    }

    @TestAttribute
    fun nativeMethodWitnessesAndGenericReferencesKeepExactLocations() {
        val empty: Array<Box<String>> = System.Array.Empty<Box<String>>()
        check(empty.size == 0)
        val repeated = System.Linq.Enumerable.Repeat(Box("repeat"), 2)
        check(System.Linq.Enumerable.Count(repeated) == 2)
        var values = arrayOf(Box("resize"))
        System.Array.Resize(byref(values), 4)
        check(values.size == 4 && values[0].value == "resize")
        check(NativeBoxApi.EchoBoxes(values) === values)
        var location = Box("before")
        touchReference(byref(location))
        check(NativeBoxApi.ReplaceAliased(byref(location), byref(location)))
        touchReference(byref(location))
        check(location.value == "second")
        val field = NativeLocationField(location)
        touchReference(byref(field.value))
        check(field.value === location)
        val capturedUpdate = { touchReference(byref(location)) }
        capturedUpdate()
        check(location.value == "second")
    }

    @TestAttribute
    fun nativeOverrideReturnsKeepTheirSelectedSlotAfterSourceForwarding() {
        check(NativeBoxApi.InvokeCreator(ForwardedNativeBoxCreator()).value == "forwarded")
    }

    @TestAttribute
    fun delegateRepresentationSeamsRespectReturnAndParameterDirection() {
        check(NativeBoxApi.InvokeFactory(NativeBoxFactory { Box("delegate") }).value == "delegate")
        val localFactory = System.Func1<Box<String>> { Box("local") }
        check(localFactory().value == "local")
        val sameEmissionFactory = System.Func1<LocalStoredBox<String>> { LocalStoredBox("same-emission") }
        check(sameEmissionFactory().value == "same-emission")
        val value = Box("consumer")
        check(NativeBoxApi.InvokeConsumer(NativeBoxConsumer { it }, value) === value)
        val covariant = System.Func1<StoredVariant<Any>>(NativeBoxApi::CreateVariant)
        check(covariant().value == 42)
        val narrowedInput = System.Func2<StoredVariant<Int>, Int>(NativeBoxApi::ReadVariant)
        check(narrowedInput(StoredVariant(42)) == 42)
    }

    @TestAttribute
    fun windowedKeepsBindingOwnedNestedCollectionArguments() {
        val windows = listOf(1, 2, 3, 4).windowed(2, 1)
        check(windows == listOf(listOf(1, 2), listOf(2, 3), listOf(3, 4)))
    }

    @TestAttribute
    @Suppress("UNCHECKED_CAST")
    fun nativeLocationsRetainExactAliasingAndCheckIncompatibleWrites() {
        val items = arrayOf(Box("array"))
        check(NativeBoxApi.EchoBoxes(items) === items && items[0].value == "array")
        val container = NativeBoxContainer(Box("container"))
        check(NativeBoxApi.EchoContainer(container) === container && container.Value.value == "container")
        check(NativeBoxApi.InvokeContainer(NativeBoxContainerFactory { container }) === container)
        var location = Box("before")
        check(NativeBoxApi.ReplaceAliased(byref(location), byref(location)))
        check(location.value == "second")
        check(try { location = Box(42) as Box<String>; false } catch (_: ClassCastException) { true })
        check(location.value == "second")
        var captured = Box("before")
        val update = { NativeBoxApi.ReplaceAliased(byref(captured), byref(captured)) }
        check(update() && captured.value == "second")
        val bad = { captured = Box(42) as Box<String> }
        check(try { bad(); false } catch (_: ClassCastException) { true })
        check(captured.value == "second")
        val field = NativeLocationField(location)
        check(NativeBoxApi.ReplaceAliased(byref(field.value), byref(field.value)))
        check(field.value.value == "second")
        check(try { field.value = Box(42) as Box<String>; false } catch (_: ClassCastException) { true })
        check(field.value.value == "second")
        NativeBoxApi.ResetLive()
        var live by byref(NativeBoxApi.Live())
        val replacement = Box("replacement")
        live = replacement
        check(NativeBoxApi.LiveValue() === replacement)
        val external = Box("external")
        NativeBoxApi.SetLive(external)
        check(live === external)
        check(try { live = Box(42) as Box<String>; false } catch (_: ClassCastException) { true })
        check(NativeBoxApi.LiveValue() === external)
    }
}
