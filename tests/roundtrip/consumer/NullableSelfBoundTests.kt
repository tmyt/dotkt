package roundtrip.nullableselfbound

import NUnit.Framework.TestAttribute

class NullableSelfBoundTests {
    @TestAttribute
    fun importedMethodsAndClosuresPreserveSelfBoundCompanions() {
        val first = Concrete(null)
        val second = Concrete(first)
        check(second.previous.value === first)
        check(wrap(first).value === first)
        check(wrapLater<String, Concrete>(second).value === second)
        check(deferred(second)().value === second)
        val factory: Factory<*> = Factory(17)
        check(factory.wrap(second).value === second)
        check(factory.tag == 17)
    }
}
