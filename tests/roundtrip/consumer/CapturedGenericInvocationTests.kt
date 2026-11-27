package roundtriptests.capturedgeneric

import NUnit.Framework.TestAttribute
import System.Collections.Generic.List as CsList
import kotlin.coroutines.CoroutineContext
import roundtrip.capturedgeneric.*

private fun readHere(context: CoroutineContext, element: CoroutineContext.Element): CoroutineContext.Element? = context[element.key]
private fun lookupHere(context: Lookup, element: Element): Element? = context.lookup(element.key)
private fun takeProjected(xs: CsList<out NativeListLeaf<*>>): NativeListLeaf<*> = takeNativeHead(xs)
private fun readWithNull(xs: CsList<out NativeListLeaf<*>>): NativeListLeaf<*> = optionalNativeHead(xs, null)
private fun appendNull(xs: CsList<out Any>) = GenericValueInterop.CollectionStorageApi.Append(xs, null)
private fun appendJoint(witness: roundtrip.constrainedbound.NativeChoice<*>, xs: CsList<out Any>) =
    RoundtripDependentNative.Selector().AppendNullable(witness, xs, null)
private fun clearProjected(xs: CsList<in NativeListLeaf<Long>>) = clearNativeList(xs)
private fun addNested(xs: CsList<NativeListRoot>, leaf: NativeListLeaf.Nested<Long>) = xs.Add(leaf)
private fun readRepeated(key: VariantKey<String, out String>): Int = repeatedKeyTag(key)
private fun <T> missingKey(): PlainKey<T>? = null
private fun <T> copyCount(key: PlainKey<T>?, xs: MutableList<T>): Int =
    GenericValueInterop.CollectionStorageApi.Copy<T>(xs).size
private fun copyUsingListWitness(xs: MutableList<*>): Int = copyCount(missingKey(), xs)
private fun <T> copyBetween(left: MutableList<T>, right: MutableList<T>, fromLeft: Boolean): Any {
    val source = if (fromLeft) left else right
    val dest = if (fromLeft) right else left
    dest.add(GenericValueInterop.CollectionStorageApi.Copy<T>(source)[0])
    return dest
}
private fun copyToFresh(xs: MutableList<*>, reverse: Boolean, useFactory: Boolean) =
    if (reverse) copyBetween(freshList(), sameList(xs), false)
    else copyBetween(sameList(xs), if (useFactory) freshList() else mutableListOf(), true)
private fun collectWithToken(key: kotlin.reflect.KClass<*>): Int = collectTokenSize(key, mutableListOf())
private fun countWithSpan(items: CsList<out System.IO.Stream>): Int =
    GenericValueInterop.CollectionStorageApi.CountWith(items, System.MemoryExtensions.AsSpan("value"))
private fun countOblivious(items: CsList<out System.IO.Stream>): Int =
    GenericValueInterop.CollectionStorageApi.CountOblivious(items)
private fun identifyWithSpan(box: GenericValueInterop.CaptureBox<out System.IO.Stream>): String =
    GenericValueInterop.CollectionStorageApi.IdentifyWith(box, null, System.MemoryExtensions.AsSpan("value"))
private class SpanCallOwner : GenericValueInterop.ProjectedListConsumer() {
    inner class Caller {
        fun clear(items: CsList<out System.IO.Stream>): Unit =
            ClearWith(items, System.MemoryExtensions.AsSpan("value"))
    }
}

private class EvaluationOrder {
    var order = ""
    fun receiver(context: CoroutineContext): CoroutineContext { order += "r"; return context }
    inner class CountedElement : CoroutineContext.Element {
        override val key: CoroutineContext.Key<*> get() { order += "k"; return FirstContextKey }
    }
}
private class ThrowingElement : CoroutineContext.Element {
    override val key: CoroutineContext.Key<*> get() = FirstContextKey
    override fun <E : CoroutineContext.Element> get(key: CoroutineContext.Key<E>): E? = throw IllegalStateException("captured-failure")
}

class CapturedGenericInvocationTests {
    @TestAttribute fun nullCannotBecomeAValueOfTheCapturedNativeType() {
        val ints = CsList<Int>()
        var rejected = false
        try { appendNull(ints) } catch (_: ClassCastException) { rejected = true }
        check(rejected && ints.Count == 0)
        val witness = roundtrip.constrainedbound.NativeChoice("joint")
        rejected = false
        try { appendJoint(witness, ints) } catch (_: ClassCastException) { rejected = true }
        check(rejected && ints.Count == 0)
        val strings = CsList<String>()
        appendNull(strings)
        appendJoint(witness, strings)
        check(strings.Count == 2)
        val nullableInts = GenericValueInterop.CollectionStorageApi.NullableIntList() as CsList<out Any>
        appendNull(nullableInts)
        check(nullableInts.Count == 1)
    }
    @TestAttribute fun projectionDoesNotBoxAnIndependentSpan() {
        check(identifyWithSpan(GenericValueInterop.CaptureBox<System.IO.MemoryStream>()) == "MemoryStream:value")
        check(countWithSpan(CsList<System.IO.Stream>()) == 0)
        val derived = CsList<System.IO.MemoryStream>()
        derived.Add(System.IO.MemoryStream())
        check(countWithSpan(derived) == 1)
        check(SpanCallOwner().Caller().clear(derived) === Unit)
        check(derived.Count == 0)
    }
    @TestAttribute fun projectedNativeListKeepsItsActualElementAndStorage() {
        check(countOblivious(CsList<System.IO.Stream>()) == 0)
        check(countOblivious(CsList<System.IO.MemoryStream>()) == 0)
        val leaf = NativeListLeaf(42L)
        val narrow = CsList<NativeListLeaf<Long>>()
        narrow.Add(leaf)
        check(readWithNull(narrow) === leaf)
        check(takeProjected(narrow) === leaf)
        check(narrow.Count == 0)

        val broad = CsList<NativeListRoot>()
        broad.Add(leaf)
        check(broad[0] === leaf)
        val nested = NativeListLeaf.Nested<Long>()
        addNested(broad, nested)
        check(broad[1] === nested)
        clearProjected(broad)
        check(broad.Count == 0)
    }
    @TestAttribute fun stdlibCapturedKeyPreservesIdentity() {
        val first = FirstContextElement()
        val second = SecondContextElement()
        check(readCapturedContext(first, first) === first)
        check(readCapturedContext(second, second) === second)
    }
    @TestAttribute fun stdlibCapturedKeyCanBeAbsent() {
        check(readCapturedContext(FirstContextElement(), SecondContextElement()) == null)
    }
    @TestAttribute fun consumerUsesTheSameCapturedFrame() {
        val first = FirstContextElement()
        check(readHere(first, first) === first)
        check(readHere(first, SecondContextElement()) == null)
    }
    @TestAttribute fun callerMethodFrameIsIndependent() {
        val first = FirstContextElement()
        check(readCapturedContextInMethod(first, first, "method-frame") === first)
        check(readCapturedContextInMethod(first, first, 42) === first)
    }
    @TestAttribute fun callerOwnerAndMethodFramesAreIndependent() {
        val first = FirstContextElement()
        check(CapturedContextReader(12).read(first, first, "method-frame") === first)
    }
    @TestAttribute fun argumentEvaluationIsOnceAndInOrder() {
        val first = FirstContextElement()
        val evaluation = EvaluationOrder()
        check(evaluation.receiver(first)[evaluation.CountedElement().key] === first)
        check(evaluation.order == "rk")
    }
    @TestAttribute fun selectedMethodExceptionsAreNotWrapped() {
        try { readHere(ThrowingElement(), FirstContextElement()); error("missing exception") }
        catch (failure: IllegalStateException) { check(failure.message == "captured-failure") }
    }
    @TestAttribute fun sourceAndReferencedDeclarationsKeepTheirOwnFrame() {
        val first = First()
        check(readCapturedLocal(first, first) === first)
        check(lookupHere(first, first) === first)
        check(lookupHere(first, Other()) == null)
    }
    @TestAttribute fun selectedStaticDeclarationUsesTheCapture() {
        val first = First()
        check(readCapturedStatic(first, first) === first)
        check(readCapturedStatic(first, Other()) == null)
    }
    @TestAttribute fun overloadSelectionDoesNotDependOnRuntimeValues() {
        val first = First()
        check(readCapturedOverload(OverloadedLookup(first), first) === first)
    }
    @TestAttribute fun unconstrainedParametersRetainTheCapture() {
        check(readPlainKey(TextKey) == "plain-key")
        val projected: PlainKey<*> = TextKey
        check(plainTag(projected) == "plain-key")
    }
    @TestAttribute fun unitReturnInvokesTheSelectedMethod() {
        val counter = KeyCounter()
        consumeCapturedKey(counter, First())
        consumeCapturedKey(counter, Other())
        check(counter.count == 2)
    }
    @TestAttribute fun privateMemberRetainsLexicalAccessAndOwnerFrame() {
        check(PrivateKeyReader("owner-frame").read(TextKey) == "private-key")
        check(PrivateKeyReader(42).read(IntegerKey) == "private-key")
    }
    @TestAttribute fun nullableValueArgumentsKeepTheirPhysicalSignature() {
        check(readNullableKey(TextKey, 7) == 7)
        check(readNullableKey(IntegerKey, null) == 0)
    }
    @TestAttribute fun independentCapturesCloseTheirOwnMethodSlots() {
        check(readTwoKeys(TextKey, IntegerKey) == "two-keys")
        check(readTwoKeys(IntegerKey, TextKey) == "two-keys")
    }
    @TestAttribute fun erasedListDoesNotConstrainAnIndependentKey() {
        check(collectHere(TextKey) == 0)
        check(collectWithToken(String::class) == 0)
        val shared = mutableListOf<Any?>("original")
        @Suppress("UNCHECKED_CAST")
        fun <T> existing(): MutableList<T> = shared as MutableList<T>
        fun clear(key: PlainKey<*>): Any = clearCollected(existing(), key)
        check(clear(IntegerKey) === shared && shared.isEmpty())
        check(copyUsingListWitness(mutableListOf(17)) == 1)
        val ints = mutableListOf(17)
        check(copyToFresh(ints, false, false) == listOf(17))
        check(copyToFresh(ints, false, true) == listOf(17))
        check(copyToFresh(ints, true, false) == listOf(17))
        check(ints == listOf(17))
    }
    @TestAttribute fun superCallKeepsNonVirtualDispatch() {
        check(DerivedKeyReader().readBase(TextKey) == "base-key")
        check(DerivedKeyReader().read(TextKey) == "derived-key")
    }
    @TestAttribute fun declaredVariantSlotsRetainAuthoredArguments() {
        val key: VariantKey<String, *> = AnyTextKey
        check(readVariantKey(key, mutableListOf("authored-string")) == 1)
        check(readRepeated(AnyTextKey) == 1)
    }
    @TestAttribute fun capturedGenericResultPreservesCarrierAndIdentity() {
        check(readCapturedKeyResult(FirstKey) === FirstKey)
        check(readCapturedKeyResult(OtherKey) === OtherKey)
        check(echoCapturedKey(FirstKey as Key<*>) === FirstKey)
        check(echoCapturedKey(OtherKey as Key<*>) === OtherKey)
    }
}
