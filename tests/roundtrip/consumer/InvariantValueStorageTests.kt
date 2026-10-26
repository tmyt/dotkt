import NUnit.Framework.TestAttribute
import GenericValueInterop.NativeBoxApi
import kotlin.clr.byref
import roundtrip.genericvalueinterop.Box
import roundtrip.invariantstorage.*

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
    }

    @TestAttribute
    @Suppress("UNCHECKED_CAST")
    fun nativeLocationsRetainExactAliasingAndCheckIncompatibleWrites() {
        val items = arrayOf(Box("array"))
        check(NativeBoxApi.EchoBoxes(items) === items && items[0].value == "array")
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
