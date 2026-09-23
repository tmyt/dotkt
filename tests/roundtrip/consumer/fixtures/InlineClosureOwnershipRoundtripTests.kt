import NUnit.Framework.TestAttribute
import roundtrip.inlineclosureownership.ClosureOwner

private fun <T> importedGenericClosure(value: T): T = ClosureOwner(value).invokeBlock { value }

private class ClosureConsumer<A : CharSequence, B>(val label: A, val value: B) {
    fun imported(): B = ClosureOwner(value).invokeBlock { check(label.length > 0); value }
    fun <M> fromMethod(value: M): M = ClosureOwner(value).invokeBlock { check(label.length > 0); value }
}

class InlineClosureOwnershipRoundtripTests {
    @TestAttribute
    fun importedClosuresSpecializeConcreteAndMethodFrames() {
        check(ClosureOwner(0).invokeBlock { 41 } == 41)
        check(ClosureOwner("").invokeBlock { "text" } == "text")
        check(importedGenericClosure(19) == 19)
        check(importedGenericClosure<String?>(null) == null)
    }

    @TestAttribute
    fun importedClosuresUseConsumerOwnerConstraintsAndSlots() {
        val receiver = ClosureConsumer("label", 23)
        check(receiver.imported() == 23)
        check(receiver.fromMethod("method") == "method")
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
