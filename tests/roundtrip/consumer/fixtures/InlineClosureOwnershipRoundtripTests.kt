import NUnit.Framework.TestAttribute
import roundtrip.inlineclosureownership.ClosureOwner

private fun <T> importedGenericClosure(value: T): T = ClosureOwner(value).invokeBlock { value }

private class ClosureValue<T>(val value: T)

private fun <M> importedConstructedClosure(value: M): M {
    val box = ClosureValue(value)
    return ClosureOwner(box).invokeBlock { box }.value
}

private class ClosureConsumer<A : CharSequence, B>(val label: A, val value: B) {
    fun imported(): B = ClosureOwner(value).invokeBlock { check(label.length > 0); value }
    fun <M> fromMethod(value: M): M = ClosureOwner(value).invokeBlock { check(label.length > 0); value }
    fun deferred(): () -> B = ClosureOwner(value).deferred { check(label.length > 0); value }
    fun <M> nested(value: M): () -> () -> M = ClosureOwner(value).nested { check(label.length > 0); value }
    fun supplier() = ClosureOwner(value).supplier { check(label.length > 0); value }
}

class InlineClosureOwnershipRoundtripTests {
    @TestAttribute
    fun importedClosuresSpecializeConcreteAndMethodFrames() {
        check(ClosureOwner(0).invokeBlock { 41 } == 41)
        check(ClosureOwner("").invokeBlock { "text" } == "text")
        check(importedGenericClosure(19) == 19)
        check(importedGenericClosure<String?>(null) == null)
        check(importedConstructedClosure(17) == 17)
        check(importedConstructedClosure("constructed") == "constructed")
        check(ClosureOwner(43).sameOwner() == 43)
    }

    @TestAttribute
    fun importedClosuresUseConsumerOwnerConstraintsAndSlots() {
        val receiver = ClosureConsumer("label", 23)
        check(receiver.imported() == 23)
        check(receiver.fromMethod("method") == "method")
        check(receiver.deferred()() == 23)
        check(receiver.nested("nested")()() == "nested")
        check(receiver.supplier().read() == 23)
    }

    @TestAttribute
    fun importedEscapingAndNestedClosuresKeepTheirFrames() {
        var value = 31
        val owner = ClosureOwner(0)
        val deferred = owner.deferred { value }
        val nested = owner.nested { value }
        val supplier = owner.supplier { value }
        value = 37
        check(deferred() == 37)
        check(nested()() == 37)
        check(supplier.read() == 37)
        check(ClosureOwner("default").fromDefault() == "default")
    }
}
