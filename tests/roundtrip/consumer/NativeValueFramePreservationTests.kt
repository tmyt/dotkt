@file:Suppress("UNCHECKED_CAST")
package roundtriptests.nativevalueframe

import NUnit.Framework.TestAttribute
import GenericValueInterop.GenericFrameApi
import GenericValueInterop.NativeCell
import GenericValueInterop.NativeValueEcho
import GenericValueInterop.NativeBoundCounter
import GenericValueInterop.NativeRefCtor
import GenericValueInterop.NativeBoundedValueEcho
import kotlin.clr.byref
import kotlin.clr.ClrRef
import kotlin.coroutines.*
import roundtrip.constructorcarrier.ExportedNativeCell
import roundtrip.constructorcarrier.StoredView
import roundtrip.constructorcarrier.BoundCounter
import roundtrip.constructorcarrier.boundCounterCount

abstract class Anchor<S : Anchor<S>>(val tag: String)
class Leaf<E>(tag: String) : Anchor<Leaf<E>>(tag)
private class Stored<T>(val value: T)
private class StoredMarker<T>(val text: String)
private fun <T> consumeStoredMarker(value: StoredMarker<T>): String = value.text
private fun <T> forwardStoredMarker(value: StoredMarker<T>): String =
    consumeStoredMarker(Stored(value).value)
private inline fun <T> readStored(value: T): T = Stored(value).value
private fun <T> forwardInlineStoredMarker(value: StoredMarker<T>): String =
    consumeStoredMarker(readStored(value))
private class StoredMarkerOwner<T> {
    private val stored = Stored(StoredMarker<T>("stored"))
    private fun consume(value: StoredMarker<T>): String = value.text
    fun read(): String = consume(stored.value)
}
private fun invokeStoredBlock(block: () -> String): String = block()
private inline fun invokeInlineStoredBlock(crossinline block: () -> String): String =
    invokeStoredBlock { block() }
private class NullableStoredMarkerOwner<T>(private val value: T) {
    private val values = System.Collections.Generic.List<T?>()
    private fun append(value: T?): String {
        values.Add(value)
        return values.Count.toString()
    }
    fun read(): String = invokeInlineStoredBlock { append(value) }
    suspend fun readSuspend(): String = invokeInlineStoredBlock { append(value) }
}
private fun readStoredImmediately(block: suspend () -> String): String {
    var observed = "not resumed"
    block.startCoroutine(object : Continuation<String> {
        override val context: CoroutineContext get() = EmptyCoroutineContext
        override fun resumeWith(result: Result<String>) { observed = result.getOrThrow() }
    })
    return observed
}
private abstract class InheritedStorageParent<P : InheritedStorageParent<P>> {
    private val value: P = this as P
    private val nullableValues = System.Collections.Generic.List<P?>()
    fun readStoredValue(): P = value
}
private abstract class InheritedStorageChild<C : InheritedStorageChild<C>> : InheritedStorageParent<C>() {
    fun read(): C = readStoredValue()
}
private class InheritedStorageLeaf : InheritedStorageChild<InheritedStorageLeaf>()
private interface StoredSink<T> {
    fun put(value: T)
    suspend fun send(value: T)
    suspend fun echo(value: T): T
}
private interface StoredProducer<T> : StoredSink<T>
private open class StoredSinkParent<T>(private var value: T) : StoredSink<T> {
    override fun put(value: T) { this.value = value }
    override suspend fun send(value: T) { this.value = value }
    override suspend fun echo(value: T): T = value
    fun read(): T = value
}
private class StoredSinkChild<T>(value: T) : StoredSinkParent<T>(value), StoredProducer<T>
private fun <T> replaceStoredMarker(slot: ClrRef<StoredMarker<T>>, value: StoredMarker<T>) {
    slot.value = value
}
private fun <T> replaceStoredMarkerLocal(value: StoredMarker<T>, replacement: StoredMarker<T>): String {
    var slot = value
    replaceStoredMarker(byref(slot), replacement)
    return slot.text
}
private interface PlainStoredBound { fun count(): Int }
private class PlainStoredCounter : PlainStoredBound { override fun count(): Int = 17 }
private fun <T : PlainStoredBound> readPlainStoredBound(value: T): Int = Stored(value).value.count()
private class StoredNativeValueEcho : NativeValueEcho {
    override fun <T> Echo(value: T): T = Stored(value).value
}
private class StoredNativeBoundedValueEcho : NativeBoundedValueEcho {
    override fun <T : BoundCounter<Int>> Echo(value: T): T = Stored(value).value
}
private fun <T : BoundCounter<Int>> readBoundCounter(value: T): Int = value.count()
private fun <T> replaceThroughConstructor(value: T, replacement: T): T {
    var slot = value
    NativeRefCtor<T>(byref(slot), replacement)
    return slot
}
private class NativeConstructorField<T>(private var value: T) {
    fun replace(replacement: T): T {
        NativeRefCtor<T>(byref(value), replacement)
        return value
    }
}
private class NativeLocalFunctionField<T>(private var value: T) {
    fun replace(replacement: T): T {
        fun assign(destination: ClrRef<T>, next: T) { destination.value = next }
        assign(byref(value), replacement)
        return value
    }
}
private class NativeLocationPause {
    var pending: Continuation<Unit>? = null
    suspend fun pause() { suspendCoroutine<Unit> { pending = it } }
}
private suspend fun <T> replaceAfterSuspension(value: T, replacement: T, gate: NativeLocationPause): T {
    gate.pause()
    GenericFrameApi.SetAndObserve<T>(byref(value), byref(value), replacement)
    return value
}
private fun <T : Appendable> appendNativeBound(value: T): T {
    value.append('x')
    return value
}
private class NativeResultStore<T>(private val value: T) {
    fun read(): T = NativeCell<T>(value).Read()
    fun readProperty(): T = NativeCell<T>(value).Value
}
private class NativeFieldStore<T>(var value: T) {
    fun replace(replacement: T): T {
        val observed = GenericFrameApi.SetAndObserve<T>(byref(value), byref(value), replacement)
        check(observed == value)
        return value
    }
}

private class NativeConstructorStore<T>(value: T, replacement: T) {
    val original = Stored(value)
    val result: Stored<T>
    init {
        var slot = value
        GenericFrameApi.SetAndObserve<T>(byref(slot), byref(slot), replacement)
        result = Stored(slot)
    }
}

private fun <T> replaceParameter(value: T, replacement: T): T {
    GenericFrameApi.SetAndObserve<T>(byref(value), byref(value), replacement)
    return value
}

private fun <T, R : StoredView<T>> replaceBoundParameter(value: R, replacement: R): T {
    val before = value.value
    GenericFrameApi.SetAndObserve<R>(byref(value), byref(value), replacement)
    check(value.value != before)
    return value.value
}

private fun <T> replaceCaptured(value: T, replacement: T): T {
    var slot = value
    val read = { slot }
    GenericFrameApi.SetAndObserve<T>(byref(slot), byref(slot), replacement)
    check(read() == replacement)
    return slot
}

private fun <R : BoundCounter<Int>> mutateBoundParameter(value: R): R {
    GenericFrameApi.SetAndObserve<R>(byref(value), byref(value), value)
    value.increment()
    return value
}

private fun <T> replaceExportedField(cell: ExportedNativeCell<T>, replacement: T): T =
    GenericFrameApi.SetAndObserve<T>(byref(cell.value), byref(cell.value), replacement)

private fun <S : Anchor<S>> replace(value: S, replacement: S): String {
    var slot = value
    val observed = GenericFrameApi.ReplaceAndObserve<S>(byref(slot), byref(slot), replacement)
    check((observed as Any) === (replacement as Any))
    return slot.tag
}

private fun <T> replaceUnbounded(value: T, replacement: T): T {
    var slot = value
    val observed = GenericFrameApi.ReplaceAndObserve<T>(byref(slot), byref(slot), replacement)
    check((observed as Any) === (replacement as Any))
    return slot
}

private fun <T> storeAndReplace(value: T, replacement: T): T {
    val stored = Stored(value)
    var slot = value
    val observed = GenericFrameApi.ReplaceAndObserve<T>(byref(slot), byref(slot), replacement)
    check((observed as Any) === (replacement as Any))
    check((stored.value as Any) === (value as Any))
    return slot
}

private fun <T> replaceAndStore(value: T, replacement: T): Stored<T> {
    var slot = value
    val observed = GenericFrameApi.ReplaceAndObserve<T>(byref(slot), byref(slot), replacement)
    check((observed as Any) === (replacement as Any))
    return Stored(slot)
}

private fun <T> writeAndReplace(value: T, before: T, replacement: T, after: T): Stored<T> {
    var slot = value
    slot = before
    val observed = GenericFrameApi.ReplaceAndObserve<T>(byref(slot), byref(slot), replacement)
    check((observed as Any?) === (slot as Any?))
    slot = after
    return Stored(slot)
}

private fun <T> setAndStore(value: T, replacement: T): Stored<T> {
    var slot = value
    val observed = GenericFrameApi.SetAndObserve<T>(byref(slot), byref(slot), replacement)
    check(observed == slot)
    return Stored(slot)
}

private fun <T> replaceReference(slot: ClrRef<T>, replacement: T): Stored<T> {
    slot.value = replacement
    return Stored(slot.value)
}

private fun <T> replaceLiveReference(value: T, replacement: T): Stored<T> {
    var slot = value
    var live by byref(GenericFrameApi.Reference<T>(byref(slot)))
    live = replacement
    val stored = Stored(live)
    check(live == slot)
    return stored
}

private fun <T> passReference(value: T, replacement: T): Stored<T> {
    val stored = Stored(value)
    var slot = value
    val observed = replaceReference<T>(byref(slot), replacement)
    check(observed.value == slot) { "managed-reference write was not observed" }
    check(stored.value == value) { "the original stored value changed" }
    return Stored(slot)
}

private class StoredCollection<T>(value: T) : AbstractMutableCollection<T>() {
    private var item: T = value
    override val size: Int get() = 1
    override fun iterator(): MutableIterator<T> = throw IllegalStateException()
    override fun add(element: T): Boolean {
        item = element
        return true
    }
    fun current(): T = item
}

class NativeValueFramePreservationTests {
    @TestAttribute
    fun nativeRefInAConstructorPreservesTheOriginalStoredValue() {
        val result = NativeConstructorStore("before", "after")
        check(result.original.value == "before")
        check(result.result.value == "after")
        check(NativeConstructorStore(10, 42).result.value == 42)
    }

    @TestAttribute
    fun nativeRefToAValueParameterUsesOneAddressableLocation() {
        check(replaceParameter("before", "after") == "after")
        check(replaceParameter(10, 42) == 42)
        check(replaceBoundParameter(roundtrip.constructorcarrier.Storage("before"),
            roundtrip.constructorcarrier.Storage("after")) == "after")
        check(boundCounterCount(mutateBoundParameter(NativeBoundCounter(0))) == 1)
    }

    @TestAttribute
    fun nativeRefMutatesTheSameLocationObservedByACapture() {
        check(replaceCaptured("before", "after") == "after")
        check(replaceCaptured(10, 42) == 42)
    }

    @TestAttribute
    fun importedNativeFieldDoesNotUseItsPrivateStorageCompanion() {
        val cell = ExportedNativeCell("before")
        check(replaceExportedField(cell, "after") == "after")
        check(cell.value == "after")
        check(cell.original == "before")
        val number = ExportedNativeCell(10)
        check(replaceExportedField(number, 42) == 42)
        check(number.value == 42)
        check(number.original == 10)
    }

    @TestAttribute
    fun nativeGenericDispatchKeepsItsPublishedFrame() {
        val echo: NativeValueEcho = StoredNativeValueEcho()
        check(echo.Echo("hello") == "hello")
        check(echo.Echo(42) == 42)
    }

    @TestAttribute
    fun nativeRefAndOutAliasTheGenericFieldItself() {
        val text = NativeFieldStore("initial")
        text.value = "before"
        check(text.replace("replacement") == "replacement")
        check(text.value == "replacement")
        text.value = "after"
        check(text.value == "after")
        val number = NativeFieldStore(10)
        check(number.replace(42) == 42)
        check(number.value == 42)
    }

    @TestAttribute
    fun aNativeFieldChecksOrdinaryWritesWithoutChangingKotlinCastSemantics() {
        val slot = NativeFieldStore(Leaf<String>("initial"))
        val incompatible = Leaf<Int>("incompatible") as Leaf<String>
        var rejected = false
        try {
            slot.value = incompatible
        } catch (_: System.InvalidCastException) {
            rejected = true
        }
        check(rejected)
        check(slot.value.tag == "initial")
        check(slot.replace(Leaf<String>("replacement")).tag == "replacement")
    }

    @TestAttribute
    fun nativeRefInsideABoundedHelperPreservesItsExactSlot() {
        check(replace(Leaf<String>("initial"), Leaf<String>("replacement")) == "replacement")
    }

    @TestAttribute
    fun nativeRefInsideAnUnboundedHelperPreservesItsExactSlot() {
        check(replaceUnbounded("initial", "replacement") == "replacement")
    }

    @TestAttribute
    fun aNativeConstructorAddressesTheOriginalGenericLocation() {
        check(replaceThroughConstructor("initial", "replacement") == "replacement")
        check(replaceThroughConstructor(10, 42) == 42)
        check(NativeConstructorField("initial").replace("replacement") == "replacement")
    }

    @TestAttribute
    fun aLocalFunctionAddressesTheOriginalGenericField() {
        check(NativeLocalFunctionField("initial").replace("replacement") == "replacement")
        check(NativeLocalFunctionField(10).replace(42) == 42)
    }

    @TestAttribute
    fun aBoundedNativeOverrideKeepsItsPrivateStorageBodyUnconstrained() {
        check(readBoundCounter(StoredNativeBoundedValueEcho().Echo(NativeBoundCounter(42))) == 42)
    }

    @TestAttribute
    fun nativeRefAndOutKeepOneLocationAcrossSuspension() {
        val gate = NativeLocationPause()
        var completed = false
        val block: suspend () -> Unit = {
            check(replaceAfterSuspension("initial", "replacement", gate) == "replacement")
        }
        block.startCoroutine(object : Continuation<Unit> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(result: Result<Unit>) { result.getOrThrow(); completed = true }
        })
        check(!completed)
        gate.pending!!.resume(Unit)
        check(completed)
    }

    @TestAttribute
    fun nativeRefAndKotlinStorageUseTheSameLogicalArgument() {
        check(storeAndReplace("initial", "replacement") == "replacement")
    }

    @TestAttribute
    fun nativeRefInsideAGenericFactoryPreservesItsExactSlot() {
        check(replaceAndStore("initial", "replacement").value == "replacement")
    }

    @TestAttribute
    fun ordinaryWritesBeforeAndAfterNativeRefUseTheSameLocation() {
        check(writeAndReplace("initial", "before", "replacement", "after").value == "after")
    }

    @TestAttribute
    fun nativeRefAndOutPreserveAliasingWithValueTypes() {
        check(setAndStore(10, 42).value == 42)
    }

    @TestAttribute
    fun nativeRefAndOutPreserveAliasingWithNullableValues() {
        check(setAndStore<String?>("initial", null).value == null)
        check(setAndStore<String?>(null, "replacement").value == "replacement")
    }

    @TestAttribute
    fun storingAValuePreservesTheInheritedCollectionSlot() {
        val values = StoredCollection("initial")
        values.add("replacement")
        check(values.current() == "replacement")
        val base: AbstractMutableCollection<String> = values
        base.add("through-base")
        check(values.current() == "through-base")
    }

    @TestAttribute
    fun aManagedReferenceParameterKeepsItsNativeElementType() {
        var slot = "initial"
        check(replaceReference(byref(slot), "replacement").value == "replacement")
        check(slot == "replacement")
    }

    @TestAttribute
    fun aLiveReferenceKeepsTheOriginalLocalLocation() {
        check(replaceLiveReference("initial", "replacement").value == "replacement")
        check(replaceLiveReference(10, 42).value == 42)
    }

    @TestAttribute
    fun aGenericKotlinHelperPassesTheExactManagedReferenceLocation() {
        check(passReference("initial", "replacement").value == "replacement")
        check(passReference(10, 42).value == 42)
    }

    @TestAttribute
    fun aNativeResultIsConvertedToTheKotlinStorageRole() {
        check(NativeResultStore("value").read() == "value")
        check(NativeResultStore(42).read() == 42)
        check(NativeResultStore("value").readProperty() == "value")
        check(NativeResultStore(42).readProperty() == 42)
    }

    @TestAttribute
    fun aStoredNativeClassBoundKeepsItsReceiverIdentity() {
        val value = StringBuilder()
        check(appendNativeBound(value) === value)
        check(value.toString() == "x")
    }

    @TestAttribute
    fun aStoredValuePreservesItsNonGenericKotlinBound() {
        check(readPlainStoredBound(PlainStoredCounter()) == 17)
    }

    @TestAttribute
    fun aStoredConstructedValueReachesItsSelectedDeclarationWithoutChangingRefStorage() {
        check(forwardStoredMarker(StoredMarker<String>("text")) == "text")
        check(forwardStoredMarker(StoredMarker<Int>("value")) == "value")
        check(forwardInlineStoredMarker(StoredMarker<String>("inline text")) == "inline text")
        check(forwardInlineStoredMarker(StoredMarker<Int>("inline value")) == "inline value")
        check(StoredMarkerOwner<String>().read() == "stored")
        check(StoredMarkerOwner<Int>().read() == "stored")
        check(NullableStoredMarkerOwner("text").read() == "1")
        check(NullableStoredMarkerOwner(42).read() == "1")
        check(readStoredImmediately { NullableStoredMarkerOwner("text").readSuspend() } == "1")
        check(readStoredImmediately { NullableStoredMarkerOwner(42).readSuspend() } == "1")
        val inherited = InheritedStorageLeaf()
        check(inherited.read() === inherited)
        check(replaceStoredMarkerLocal(StoredMarker<String>("before"), StoredMarker<String>("after")) == "after")
        check(replaceStoredMarkerLocal(StoredMarker<Int>("before"), StoredMarker<Int>("after")) == "after")
    }

    @TestAttribute
    fun inheritedSuspendDeclarationsFillEveryPhysicalInterfaceSlot() {
        val strings = StoredSinkChild("before")
        val producer: StoredProducer<String> = strings
        producer.put("middle")
        check(strings.read() == "middle")
        check(readStoredImmediately {
            producer.send(producer.echo("after"))
            strings.read()
        } == "after")
        val values = StoredSinkChild(1)
        val valueProducer: StoredProducer<Int> = values
        valueProducer.put(2)
        check(readStoredImmediately {
            valueProducer.send(valueProducer.echo(3))
            values.read().toString()
        } == "3")
    }

    @TestAttribute
    fun anUncheckedKotlinCastDoesNotRelaxTheNativeLocationType() {
        // The ordinary Kotlin cast keeps its raw-classifier semantics. The
        // subsequent native location requires the exact CLR construction.
        val incompatible = Leaf<Int>("initial") as Leaf<String>
        var rejected = false
        try {
            replaceAndStore(incompatible, Leaf<String>("replacement"))
        } catch (_: System.InvalidCastException) {
            rejected = true
        }
        check(rejected) { "an incompatible CLR construction entered a native ref location" }
    }
}
