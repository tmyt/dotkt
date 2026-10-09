package roundtriptests.capturedgeneric

import NUnit.Framework.TestAttribute
import kotlin.coroutines.CoroutineContext
import roundtrip.capturedgeneric.*

private fun readHere(context: CoroutineContext, element: CoroutineContext.Element): CoroutineContext.Element? = context[element.key]
private fun lookupHere(context: Lookup, element: Element): Element? = context.lookup(element.key)

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
    @TestAttribute fun superCallKeepsNonVirtualDispatch() {
        check(DerivedKeyReader().readBase(TextKey) == "base-key")
        check(DerivedKeyReader().read(TextKey) == "derived-key")
    }
    @TestAttribute fun declaredVariantSlotsRetainAuthoredArguments() {
        val key: VariantKey<String, *> = AnyTextKey
        check(readVariantKey(key, mutableListOf("authored-string")) == 1)
    }
    @TestAttribute fun capturedGenericResultPreservesCarrierAndIdentity() {
        check(readCapturedKeyResult(FirstKey) === FirstKey)
        check(readCapturedKeyResult(OtherKey) === OtherKey)
    }
}
