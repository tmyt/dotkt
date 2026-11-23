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
private fun mutateBeforeNativeBoundary(cross: Boolean, change: (Array<Box<String>>) -> Unit): Any {
    val items = arrayOf(Box("before"))
    change(items)
    if (cross) NativeBoxApi.EchoBoxes(items)
    return items[0]
}
private class DelegatedVector : GenericValueInterop.NativeVectorBase(arrayOf(roundtrip.genericvalueinterop.Derived<String>()))
private class DelegatedBoxVector(value: Box<String>) : GenericValueInterop.NativeBoxArrayLease(arrayOf(value))
private class LocalStoredBox<T>(val value: T)
private open class PlainArrayBase
private class ProjectedArrayChild<T>(val value: T) : PlainArrayBase()
private class NativeRowSource<T>(val value: T) : GenericValueInterop.NativeValueSource<T> {
    override fun Read(): T = value
}
private fun readMixed(vararg values: LocalStoredBox<String>): String = values[0].value + values[1].value
private interface LocalStoredCallback<T> { fun apply(value: T, transform: (T) -> Any): String }
private class LocalStoredIntCallback : LocalStoredCallback<Int> {
    override fun apply(value: Int, transform: (Int) -> Any): String = transform(value).toString()
}
private fun <C : LocalStoredCallback<Int>> invokeStoredCallback(callback: C): String =
    callback.apply(1) { "$it!" }
private class ForwardedNativeBoxCreator : NativeBoxCreator {
    override fun Create(): Box<String> = forwardedNativeBox()
}

class InvariantValueStorageTests {
    @TestAttribute
    @Suppress("UNCHECKED_CAST")
    fun ordinaryArrayAccessUsesTheActualComponentOfACovariantAlias() {
        val other = ProjectedArrayChild(42)
        fun replaceAndRead(values: Array<PlainArrayBase>) {
            values[0] = other
            check(values[0] === other)
            for (value in values) check(value === other)
        }
        replaceAndRead(arrayOf(PlainArrayBase()))
        val typed = arrayOf(ProjectedArrayChild("first"))
        val widened = typed as Array<PlainArrayBase>
        replaceAndRead(widened)
        check((typed[0] as Any) === other)
        check(try { widened[0] = PlainArrayBase(); false } catch (_: System.ArrayTypeMismatchException) { true })
        check(widened[0] === other)
    }

    @TestAttribute
    @Suppress("UNCHECKED_CAST")
    fun reifiedRowsRetainTheirNativeComponentAndPublishOnlyAtClrBoundaries() {
        val original = Box("first")
        val row = roundtrip.genericvalueinterop.reifiedVector(original)
        val other = Box(42)
        row[0] = other as Box<String>
        val matrix = arrayOf(arrayOf<Any?>(null))
        fun widenRow(r: Array<Box<String>>): Array<Any?> = r as Array<Any?>
        matrix[0] = widenRow(row)
        check(matrix[0][0] === other)
        val nativeRows = arrayOf(row as System.Array)
        check(try { NativeBoxApi.ReadArrayRows(nativeRows); false } catch (_: ClassCastException) { true })
        val source = NativeRowSource(row as System.Array)
        check(try { NativeBoxApi.ReadGenericSource(source); false } catch (_: ClassCastException) { true })
        val kotlinSupplier = { row }
        val nativeSupplier = GenericValueInterop.NativeRowFactory { row }
        check((kotlinSupplier()[0] as Any) === other)
        check(try { NativeBoxApi.ReadFactoryRow(nativeSupplier); false } catch (_: ClassCastException) { true })
        check(try { GenericValueInterop.NativeRowComparer(1).compareTo(row); false } catch (_: ClassCastException) { true })
        row[0] = original
        check(NativeBoxApi.EchoBoxes(row) === row && NativeBoxApi.ReadArrayRows(nativeRows) === original)
        check(NativeBoxApi.ReadFactoryRow(nativeSupplier) === original)
    }

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
        val tail = arrayOf(LocalStoredBox("tail"))
        check(readMixed(LocalStoredBox("head"), *tail) == "headtail")
        val elements = storedList()
        check(elements[0] === UniversalStoredView && elements[0].code(42) == 7)
        val pair = storedPair()
        check(pair.first === UniversalStoredView && pair.first.code(42) == 7 && pair.second == 1)
        val bag = mutableListOf<Any>()
        val readFirst = NativeBoxApi.RetainBagReader(bag)
        val box = Box("after publication")
        bag.add(arrayOf(box))
        check(readFirst() === box)
    }

    @TestAttribute
    fun projectedManagedReferenceSlotsRetainTheirCarrierPointee() {
        val first = Box(1)
        var slot: Box<*> = first
        check(slot === first) // narrows the value read, not the writable slot
        val replacement: Box<*> = Box("after")
        var evaluations = 0
        fun next(): Box<*> { evaluations++; return replacement }
        replaceProjectedSlot(byref(slot), next())
        fun contents(box: Box<*>): Any? = box.value
        check(evaluations == 1 && slot === replacement && contents(slot) == "after")
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
        check(DelegatedVector().Count == 1)
        val element = Box("one")
        check(DelegatedBoxVector(element).First === element)
        val empty: Array<Box<String>> = System.Array.Empty<Box<String>>()
        check(empty.size == 0)
        check(NativeBoxApi.IsNativeBoxMatrix(System.Array.Empty<Array<Box<String>>>()))
        val repeated = System.Linq.Enumerable.Repeat(Box("repeat"), 2)
        check(System.Linq.Enumerable.Count(repeated) == 2)
        var values = arrayOf(Box("resize"))
        values[0] = forwardedNativeBox()
        System.Array.Resize(byref(values), 4)
        check(values.size == 4 && values[0].value == "forwarded")
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
        check(invokeStoredCallback(LocalStoredIntCallback()) == "1!")
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
    fun exportingAnElementAddressDoesNotExportItsSiblings() {
        val first = Box("first")
        val slots = arrayOf(first, first)
        val other = arrayOf(first)
        val wrong = Box(42)
        val erased = wrong as Box<String>
        other[0] = erased
        check(try { NativeBoxApi.ContainsSlot(byref(slots[0]), other); false } catch (_: ClassCastException) { true })
        slots[0] = erased // A rejected call did not publish the earlier operand.
        check(try { NativeBoxApi.ReplaceAliased(byref(slots[0]), byref(slots[0])); false } catch (_: ClassCastException) { true })
        check((slots[0] as Any) === wrong)
        slots[0] = first
        slots[1] = erased
        check(NativeBoxApi.ReplaceAliased(byref(slots[0]), byref(slots[0])))
        check(slots[0].value == "second" && (slots[1] as Any) === wrong)
        check(try { slots[0] = erased; false } catch (_: ClassCastException) { true })
        slots[1] = first
        slots[1] = erased
        check((slots[1] as Any) === wrong)
    }

    @TestAttribute
    @Suppress("UNCHECKED_CAST")
    fun nativeLocationsRetainExactAliasingAndCheckIncompatibleWrites() {
        val items = arrayOf(forwardedNativeBox())
        val alias = items
        check(NativeBoxApi.EchoBoxes(alias) === items && items[0].value == "forwarded")
        val number = Box(42)
        val erased = number as Box<String>
        check(mutateBeforeNativeBoundary(false) { it[0] = erased } === number)
        check(mutateBeforeNativeBoundary(true) { } is Box<*>)
        val retained = roundtrip.genericvalueinterop.boxVector(forwardedNativeBox())
        roundtrip.genericvalueinterop.replaceFirstElement(retained, erased)
        check((roundtrip.genericvalueinterop.firstArrayElement(retained) as Any) === number)
        check((roundtrip.genericvalueinterop.firstIteratedElement(retained) as Any) === number)
        fun widen(values: Array<Box<String>>): Array<Any?> = values as Array<Any?>
        val widened = widen(retained)
        check(widened[0] === number)
        check(try { widened[0] = "unrelated"; false } catch (_: System.ArrayTypeMismatchException) { true })
        check(widened[0] === number)
        widened[0] = forwardedNativeBox()
        widened[0] = number
        check((roundtrip.genericvalueinterop.firstArrayElement(retained) as Any) === number)
        val copy = retained.copyOf()
        check(copy !== retained && (copy[0] as Any) === number)
        val packed = arrayOf(*retained, forwardedNativeBox().also { retained[0] = it })
        check((packed[0] as Any) === number) // spread was evaluated before the following mutation
        retained[0] = erased
        packed[0] = packed[1]
        check(NativeBoxApi.EchoBoxes(packed) === packed)
        val grown = roundtrip.arrayconversion.LibraryArrayCollection(listOf<Any?>(number, number)).into(arrayOf(forwardedNativeBox()))
        check(grown.size == 2 && (grown[1] as Any) === number)
        check(try { NativeBoxApi.EchoBoxes(retained); false } catch (_: ClassCastException) { true })
        check(try { NativeBoxApi.ArrayField = retained; false } catch (_: ClassCastException) { true })
        check(try { NativeBoxApi.ArrayProperty = retained; false } catch (_: ClassCastException) { true })
        check(try { (retained as System.Array).GetValue(0); false } catch (_: ClassCastException) { true })
        check(try { NativeBoxContainer<Any>(retained); false } catch (_: ClassCastException) { true })
        check(try { roundtrip.parameterslots.takeRef(byref(retained[0]), forwardedNativeBox()); false } catch (_: ClassCastException) { true })
        var arrayLocation by byref(NativeBoxApi.ArrayLocation())
        check(try { arrayLocation = retained; false } catch (_: ClassCastException) { true })
        val source = object : GenericValueInterop.NativeBoxArraySource {
            override fun Read(): Array<Box<String>> = retained
        }
        check(try { NativeBoxApi.ReadFirst(source); false } catch (_: ClassCastException) { true })
        check(try { NativeBoxApi.IsNativeBoxMatrix(retained); false } catch (_: ClassCastException) { true })
        val repaired = Box("repair")
        retained[0] = repaired
        val lease = NativeBoxApi.RetainBoxes(retained)
        check(lease.ArrayIdentity === retained && lease.First === repaired)
        val fromNative = Box("native")
        lease.First = fromNative
        check(retained[0] === fromNative)
        check(try { retained[0] = erased; false } catch (_: ClassCastException) { true })
        check(lease.First === fromNative)
        check((copy[0] as Any) === number)
        var locationArray = arrayOf(forwardedNativeBox())
        val oldArray = locationArray
        oldArray[0] = erased
        val unexposed = arrayOf(forwardedNativeBox())
        check(try { NativeBoxApi.CountBoth(unexposed, oldArray); false } catch (_: ClassCastException) { true })
        unexposed[0] = erased
        check((unexposed[0] as Any) === number)
        check(try { NativeBoxApi.FirstMissing(byref(locationArray)); false } catch (_: ClassCastException) { true })
        NativeBoxApi.ResetBoxes(byref(locationArray))
        check(locationArray[0].value == "reset" && (oldArray[0] as Any) === number)
        val foreignCopy = locationArray.copyOf()
        foreignCopy[0] = erased
        check((foreignCopy[0] as Any) === number && locationArray[0].value == "reset")
        val matrix = arrayOf(oldArray)
        check(try { NativeBoxApi.ReadFirstRow(matrix); false } catch (_: ClassCastException) { true })
        oldArray[0] = repaired
        check(NativeBoxApi.ReadFirstRow(matrix) === repaired)
        check(try { oldArray[0] = erased; false } catch (_: ClassCastException) { true })
        check(try { matrix[0] = unexposed; false } catch (_: ClassCastException) { true })
        check(matrix[0] === oldArray)
        unexposed[0] = repaired
        matrix[0] = unexposed
        check(matrix[0] === unexposed && NativeBoxApi.ReadFirstRow(matrix) === repaired)
        check(try { unexposed[0] = erased; false } catch (_: ClassCastException) { true })
        fun nested(cross: Boolean): Any {
            val items = arrayOf(forwardedNativeBox())
            val outer = arrayOf(items)
            outer[0][0] = erased
            if (cross) NativeBoxApi.EchoBoxes(items)
            return items[0]
        }
        check(nested(false) === number)
        check(try { nested(true); false } catch (_: ClassCastException) { true })
        val kotlinItems = arrayOf(Box("initial"))
        val writableAlias = kotlinItems
        writableAlias[0] = erased
        check((kotlinItems[0] as Any) === number)
        check(try { NativeBoxApi.EchoBoxes(kotlinItems); false } catch (_: ClassCastException) { true })
        check((kotlinItems[0] as Any) === number)
        val container = NativeBoxContainer(Box("container"))
        fun initialized(badIndex: Int) {
            var calls = 0
            val initial = forwardedNativeBox()
            val a = Array(3) { i -> check(i == calls++); if (i == badIndex) erased else initial }
            check(calls == 3 && a[0] === initial)
            if (badIndex < 0) check(NativeBoxApi.EchoBoxes(a) === a)
            else {
                check((a[badIndex] as Any) === number)
                check(try { NativeBoxApi.EchoBoxes(a); false } catch (_: ClassCastException) { true })
            }
        }
        initialized(-1)
        initialized(2)
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
