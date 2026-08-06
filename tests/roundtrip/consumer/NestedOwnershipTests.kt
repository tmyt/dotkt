import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import roundtrip.ownership.Owner
import roundtrip.ownership.OwnedRichEnum
import roundtrip.ownership.OwnedRichLambdaEnum
import roundtrip.ownership.SparseGenericSuspendOwner
import roundtrip.ownership.sparseGenericSuspend
import roundtrip.ownership.sparseGenericLocalFunction
import roundtrip.ownership.nestedGenericLocalFunction
import roundtrip.ownership.defaultLambdaWithLocalFunction
import roundtrip.ownership.defaultCapturingLambdaWithLocalFunction
import roundtrip.ownership.localFunctionWithLocalClass
import roundtrip.ownership.GenericInnerLocalOwner
import roundtrip.ownership.GenericMemberDefaultOwner
import roundtrip.ownership.PlainNestedEnum
import roundtrip.ownership.ProtectedNestedOwner
import roundtrip.ownership.invokeTwice
import roundtrip.ownership.inlineOwnedReader
import roundtrip.ownership.localSuspendFunctionReference
import roundtrip.ownership.makeNested
import roundtrip.ownership.makeMultiLevelInner
import roundtrip.ownership.GenericNestedClosureOwner
import roundtrip.ownership.InitLocalFunctionOwner
import roundtrip.ownership.inlineNestedClosure
import roundtrip.ownership.makeShadowedInner
import roundtrip.ownership.shadowedInnerTypeParameters
import roundtrip.ownership.topLevelLocalValue
import roundtrip.ownership.accessorClosureValue
import roundtrip.ownership.defaultInterfaceClosureValue
import mpp.app.passShadowedInner

private class InlineOwnershipConsumer(private val value: String) {
    fun read(): String = inlineOwnedReader { value }.read()
}

private class GenericInlineOwnershipConsumer<A>(private val prefix: A) {
    fun <B> read(value: B): String = inlineOwnedReader { prefix.toString() + ":" + value.toString() }.read()
}

private fun <T> invokeTwiceWithLocalFunction(value: T): Pair<T, T> = invokeTwice {
    fun read(): T = value
    read()
}

class NestedOwnershipRoundtripTests {
    @TestAttribute
    fun nestedInnerLocalAndAnonymousOwnershipRoundTrip() {
        val owner = Owner(123)
        val nested: Owner.Nested = makeNested(4)
        val inner: Owner<Int>.Inner = owner.Inner(456)

        ClassicAssert.AreEqual(8, nested.doubled())
        val multi: roundtrip.ownership.MultiLevelInnerOwner<Int>.Middle<String>.Leaf<Int> = makeMultiLevelInner()
        ClassicAssert.AreEqual("56:middle:57", multi.render())
        ClassicAssert.AreEqual("56:middle:57", multi.localRender())
        ClassicAssert.AreEqual("123:456", inner.joined())
        ClassicAssert.AreEqual(6, owner.localClassValue(3))
        ClassicAssert.AreEqual("123", owner.anonymousValue().read())
        ClassicAssert.AreEqual("123", owner.closureValue()())
        ClassicAssert.AreEqual("123!", owner.localFunctionValue("!"))
        ClassicAssert.AreEqual("123:7", owner.nestedLocalTypeFrames())
        ClassicAssert.AreEqual(123, runCrossModuleSuspend(owner.localSuspendValue()))
        ClassicAssert.AreEqual(124, runCrossModuleSuspend(Owner("outer").genericLocalSuspendValue(124)))
        ClassicAssert.AreEqual(125, runCrossModuleSuspend(Owner("outer").shadowedGenericLocalSuspend(125)))
        ClassicAssert.AreEqual(10, owner.localFunctionFromClosure(9)())
        ClassicAssert.AreEqual(11, owner.localFunctionFromLocalClass(10))
        ClassicAssert.AreEqual("123", owner.localFunctionFromGenericLocal())
        ClassicAssert.AreEqual("123:7", owner.localFunctionInsideGenericLocal())
        ClassicAssert.AreEqual("123", owner.localGenericOwnArgumentMatchesCapture())
        ClassicAssert.AreEqual("shadow", owner.shadowedGenericClosure("shadow")())
        ClassicAssert.AreEqual(42, runCrossModuleSuspend(SparseGenericSuspendOwner<String, Int>(42).make()))
        ClassicAssert.AreEqual(43, runCrossModuleSuspend(sparseGenericSuspend<String, Int>(43)))
        ClassicAssert.AreEqual(44, sparseGenericLocalFunction<String, Int>(44))
        ClassicAssert.AreEqual("nested:47", nestedGenericLocalFunction("nested"))
        ClassicAssert.AreEqual(48, defaultLambdaWithLocalFunction())
        ClassicAssert.AreEqual(48, defaultLambdaWithLocalFunction())
        ClassicAssert.AreEqual(52, defaultCapturingLambdaWithLocalFunction(52))
        ClassicAssert.AreEqual(53, defaultCapturingLambdaWithLocalFunction(53))
        ClassicAssert.AreEqual("49", localFunctionWithLocalClass<Unit, Int>(49))
        ClassicAssert.AreEqual("outer:50", GenericInnerLocalOwner("outer").Entry(50).render())
        ClassicAssert.AreEqual(51, PlainNestedEnum.Helper(51).value)
        val repeatedLocal = invokeTwice {
            fun increment(): Int = 45
            increment()
        }
        ClassicAssert.AreEqual(45, repeatedLocal.first)
        ClassicAssert.AreEqual(45, repeatedLocal.second)
        ClassicAssert.AreEqual(46, runCrossModuleSuspend(localSuspendFunctionReference(46)))
        val genericRepeatedLocal = invokeTwiceWithLocalFunction(55)
        ClassicAssert.AreEqual(55, genericRepeatedLocal.first)
        ClassicAssert.AreEqual(55, genericRepeatedLocal.second)
        ClassicAssert.AreEqual(17, OwnedRichEnum.FIRST.marker())
        ClassicAssert.AreEqual(61, OwnedRichLambdaEnum.FIRST.reader()())
        ClassicAssert.AreEqual(60, GenericNestedClosureOwner<String>().factory()(60)())
        ClassicAssert.AreEqual(62, InitLocalFunctionOwner(0).value)
        ClassicAssert.AreEqual(62, InitLocalFunctionOwner("unused").value)
        ClassicAssert.AreEqual("inline!", inlineNestedClosure { it + "!" }())
        ClassicAssert.AreEqual("7:seven", shadowedInnerTypeParameters())
        val shadowed: roundtrip.ownership.ShadowOwner<Int>.Entry<String> = makeShadowedInner()
        ClassicAssert.AreEqual("8:eight", shadowed.joined())
        ClassicAssert.AreSame(shadowed, passShadowedInner(shadowed))
        ClassicAssert.AreEqual(9, topLevelLocalValue(9))
        ClassicAssert.AreEqual("accessor", accessorClosureValue())
        ClassicAssert.AreEqual("default", defaultInterfaceClosureValue())
        ClassicAssert.AreEqual("member-default", GenericMemberDefaultOwner("member-default").render())
        ClassicAssert.AreEqual(19, ProtectedNestedOwner().value())
        ClassicAssert.AreEqual("consumer", InlineOwnershipConsumer("consumer").read())
        ClassicAssert.AreEqual("generic:54", GenericInlineOwnershipConsumer("generic").read(54))
    }
}
